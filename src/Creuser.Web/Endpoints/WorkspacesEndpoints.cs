using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Repositories;
using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Contracts.Responses;
using Creuser.Web.Environment;
using Creuser.Web.Workspaces;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public sealed record TestWorkspaceConnectionRequest(
    string Type,
    GitWorkspaceSettingsDto? GitSettings = null,
    LocalWorkspaceSettingsDto? LocalSettings = null
);

public sealed record WorkspaceConnectionTestResult(bool Ok, long LatencyMs, string? Error);

public static class WorkspacesEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Single shared HttpClient for git-protocol probes. 10-second timeout
    // since git smart-HTTP responses are usually quick; longer would mean
    // the user's network is the problem and they need to know.
    private static readonly HttpClient _gitTestHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Per-slug semaphore so concurrent sync requests for the same workspace
    // serialize (clone+fetch on one slug at a time), but different slugs
    // sync in parallel. In-memory only — multi-instance deployments would
    // need a Postgres advisory lock here. Single-tenant on-prem v1 is fine.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        SemaphoreSlim
    > _syncLocks = new();

    public static IEndpointRouteBuilder MapWorkspacesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces")
            .WithTags("Workspaces")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", (Delegate)List).WithName("ListWorkspaces");
        group.MapGet("/{slug}", (Delegate)GetBySlug).WithName("GetWorkspace");
        group.MapPost("/", (Delegate)Create).WithName("CreateWorkspace");
        group.MapPut("/{slug}", (Delegate)Update).WithName("UpdateWorkspace");
        group.MapDelete("/{slug}", (Delegate)Delete).WithName("DeleteWorkspace");
        group.MapPost("/test", (Delegate)TestConnection).WithName("TestWorkspaceConnection");
        group.MapPost("/{slug}/sync", (Delegate)Sync).WithName("SyncWorkspace");

        return app;
    }

    private static async Task<Ok<ApiResult<IReadOnlyList<WorkspaceResult>>>> List(
        IWorkspaceStore store,
        SecretsService secrets,
        int? skip,
        int? take
    )
    {
        var rows = await store.ListAsync(Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 200));
        IReadOnlyList<WorkspaceResult> result = rows.Select(w => ToResult(w, secrets)).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<WorkspaceResult>>(result));
    }

    private static async Task<Results<Ok<ApiResult<WorkspaceResult>>, ProblemHttpResult>> GetBySlug(
        string slug,
        IWorkspaceStore store,
        SecretsService secrets
    )
    {
        var ws = await store.FindBySlugAsync(slug);
        return ws is null
            ? Problems.WorkspaceNotFound(slug)
            : TypedResults.Ok(new ApiResult<WorkspaceResult>(ToResult(ws, secrets)));
    }

    private static async Task<Results<Ok<ApiResult<WorkspaceResult>>, ProblemHttpResult>> Create(
        CreateWorkspaceRequest request,
        IValidator<CreateWorkspaceRequest> validator,
        IWorkspaceStore store,
        SecretsService secrets,
        TimeProvider time,
        HttpContext http,
        CancellationToken ct
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Problems.ValidationFailed(AuthEndpoints.ToErrorMap(validation));

        if (await store.SlugExistsAsync(request.Slug))
            return Problems.SlugAlreadyExists(request.Slug);

        string settingsJson;
        if (request.Type == WorkspaceType.Git && request.GitSettings is not null)
        {
            // Persist the inline credential (if any) before writing the
            // workspace record — that way the saved AuthSecret filename
            // always points at an actual on-disk secret. Keeping the
            // credential out of the DB by moving it through SecretsService
            // is the architecture's secret discipline applied to workspaces.
            var authSecret = await PersistInlineCredentialAsync(
                request.Slug,
                request.GitSettings.AuthMode,
                request.GitSettings.AuthCredential,
                secrets,
                ct
            );
            var gs = new GitWorkspaceSettings(
                RepositoryUrl: request.GitSettings.RepositoryUrl,
                AuthMode: request.GitSettings.AuthMode,
                AuthSecret: authSecret,
                WorkingBranch: request.GitSettings.WorkingBranch,
                SourceBranch: request.GitSettings.SourceBranch,
                Mode: request.GitSettings.Mode,
                PushFrequency: request.GitSettings.PushFrequency
            );
            settingsJson = JsonSerializer.Serialize(gs, JsonOpts);
        }
        else if (request.Type == WorkspaceType.Local && request.LocalSettings is not null)
        {
            var ls = new LocalWorkspaceSettings(
                Path: request.LocalSettings.Path,
                Writable: request.LocalSettings.Writable
            );
            settingsJson = JsonSerializer.Serialize(ls, JsonOpts);
        }
        else
        {
            // Validator should have caught this; defense in depth.
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["type"] = ["Settings missing for the requested workspace type."],
                }
            );
        }

        var now = time.GetUtcNow().UtcDateTime;
        var workspace = new Workspace(
            Id: Guid.NewGuid(),
            Slug: request.Slug,
            Name: request.Name,
            Description: request.Description,
            Type: request.Type,
            Settings: settingsJson,
            CreatedAt: now,
            UpdatedAt: now,
            CreatedBy: CookieAuthHelpers.GetUserId(http)
        );
        await store.SaveAsync(workspace);
        return TypedResults.Ok(new ApiResult<WorkspaceResult>(ToResult(workspace, secrets)));
    }

    private static async Task<Results<Ok<ApiResult<WorkspaceResult>>, ProblemHttpResult>> Update(
        string slug,
        UpdateWorkspaceRequest request,
        IValidator<UpdateWorkspaceRequest> validator,
        IWorkspaceStore store,
        SecretsService secrets,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Problems.ValidationFailed(AuthEndpoints.ToErrorMap(validation));

        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);

        // If the type changed (e.g. local → git), the previous type's
        // resources need cleanup. For now: dropping a git workspace's auth
        // secret. Local has no on-disk resources owned by Creuser to clean.
        if (existing.Type != request.Type && existing.Type == WorkspaceType.Git)
        {
            var oldGit = ParseGitSettings(existing.Settings);
            if (!string.IsNullOrWhiteSpace(oldGit?.AuthSecret))
                secrets.Delete(oldGit.AuthSecret);
        }

        string settingsJson;
        if (request.Type == WorkspaceType.Git && request.GitSettings is not null)
        {
            // Credential update rules (only relevant for git):
            //   1. Mode is None → delete any old secret.
            //   2. AuthCredential supplied (rotation) → overwrite the secret.
            //   3. Mode unchanged + AuthCredential null → keep existing AuthSecret reference.
            //   4. Type changed FROM something else → there's no existing git secret.
            var existingGit =
                existing.Type == WorkspaceType.Git ? ParseGitSettings(existing.Settings) : null;
            var authSecret = existingGit?.AuthSecret;

            if (request.GitSettings.AuthMode == GitAuthMode.None)
            {
                if (!string.IsNullOrWhiteSpace(authSecret))
                    secrets.Delete(authSecret);
                authSecret = null;
            }
            else if (!string.IsNullOrWhiteSpace(request.GitSettings.AuthCredential))
            {
                authSecret = await PersistInlineCredentialAsync(
                    slug,
                    request.GitSettings.AuthMode,
                    request.GitSettings.AuthCredential,
                    secrets,
                    ct
                );
            }
            else if (request.GitSettings.AuthMode != existingGit?.AuthMode)
            {
                authSecret = null;
            }

            var gs = new GitWorkspaceSettings(
                RepositoryUrl: request.GitSettings.RepositoryUrl,
                AuthMode: request.GitSettings.AuthMode,
                AuthSecret: authSecret,
                WorkingBranch: request.GitSettings.WorkingBranch,
                SourceBranch: request.GitSettings.SourceBranch,
                Mode: request.GitSettings.Mode,
                PushFrequency: request.GitSettings.PushFrequency
            );
            settingsJson = JsonSerializer.Serialize(gs, JsonOpts);
        }
        else if (request.Type == WorkspaceType.Local && request.LocalSettings is not null)
        {
            var ls = new LocalWorkspaceSettings(
                Path: request.LocalSettings.Path,
                Writable: request.LocalSettings.Writable
            );
            settingsJson = JsonSerializer.Serialize(ls, JsonOpts);
        }
        else
        {
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["type"] = ["Settings missing for the requested workspace type."],
                }
            );
        }

        var updated = existing with
        {
            Name = request.Name,
            Description = request.Description,
            Type = request.Type,
            Settings = settingsJson,
            UpdatedAt = time.GetUtcNow().UtcDateTime,
        };
        await store.SaveAsync(updated);
        return TypedResults.Ok(new ApiResult<WorkspaceResult>(ToResult(updated, secrets)));
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> Delete(
        string slug,
        IWorkspaceStore store,
        SecretsService secrets,
        WorkspaceFilesystemService fs,
        CancellationToken ct
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);

        // Clean up the auth secret on disk too — only relevant for git
        // workspaces. Local workspaces don't own any Creuser-managed
        // secrets; the path itself stays on disk after the workspace row
        // is gone (admin's responsibility to remove the actual files).
        if (existing.Type == WorkspaceType.Git)
        {
            var gs = ParseGitSettings(existing.Settings);
            if (!string.IsNullOrWhiteSpace(gs?.AuthSecret))
                secrets.Delete(gs.AuthSecret);
            // Reclaim the cloned working tree under <dataDir>/workspaces/<slug>/.
            // Best-effort — admins can rm -rf the directory by hand if this fails.
            await fs.RemoveWorkingTreeAsync(slug, ct);
        }

        var deleted = await store.DeleteAsync(existing.Id);
        return deleted
            ? TypedResults.Ok(new ApiResult<bool>(true))
            : Problems.WorkspaceNotFound(slug);
    }

    private static async Task<Ok<ApiResult<WorkspaceConnectionTestResult>>> TestConnection(
        TestWorkspaceConnectionRequest request,
        SecretsService secrets,
        ILogger<TestConnectionMarker> logger,
        CancellationToken ct
    )
    {
        try
        {
            return await TestConnectionCore(request, secrets, ct);
        }
        catch (Exception ex)
        {
            // Log + surface a useful message instead of letting the
            // request hit ASP.NET's default 500 handler (which masks
            // diagnostic information).
            logger.LogError(ex, "Workspace test-connection failed");
            return Reply(false, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Marker type so the endpoint can ask DI for an ILogger&lt;T&gt; with a meaningful category name.</summary>
    private sealed class TestConnectionMarker { }

    private sealed class SyncMarker { }

    /// <summary>
    /// Sync the workspace's content. For git: clone (first time) or fetch +
    /// reset --hard origin/&lt;workingBranch&gt; (subsequent). For local: verify
    /// the path is still readable. Per-slug serialization via in-memory
    /// semaphore — concurrent calls for the same slug queue, different slugs
    /// run in parallel.
    /// </summary>
    private static async Task<Results<Ok<ApiResult<WorkspaceSyncResult>>, ProblemHttpResult>> Sync(
        string slug,
        IWorkspaceStore store,
        SecretsService secrets,
        WorkspaceFilesystemService fs,
        TimeProvider time,
        ILogger<SyncMarker> logger,
        CancellationToken ct,
        // Two-phase confirmation: a first call with force=false returns
        // RequiresForce=true (and the dirty count) when the working tree has
        // uncommitted changes. The SPA confirms with the admin and retries
        // with force=true to actually discard.
        bool force = false
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);

        var gate = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            WorkspaceSyncResult result;
            try
            {
                result = existing.Type switch
                {
                    WorkspaceType.Git => await SyncGitAsync(
                        existing,
                        secrets,
                        fs,
                        time,
                        sw,
                        force,
                        ct
                    ),
                    WorkspaceType.Local => SyncLocal(existing, time, sw),
                    _ => new WorkspaceSyncResult(
                        Ok: false,
                        Slug: slug,
                        Sha: null,
                        LatencyMs: 0,
                        SyncedAt: time.GetUtcNow().UtcDateTime,
                        Message: null,
                        Error: $"Sync not supported for type '{existing.Type}'."
                    ),
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(ex, "Workspace sync failed for {Slug}", slug);
                result = new WorkspaceSyncResult(
                    Ok: false,
                    Slug: slug,
                    Sha: null,
                    LatencyMs: sw.ElapsedMilliseconds,
                    SyncedAt: time.GetUtcNow().UtcDateTime,
                    Message: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}"
                );
            }

            // Persist the sync state regardless of outcome — operators want
            // to see "last attempt failed at <time>" in the UI, not just a
            // stale "ok" from a week ago. RequiresForce refusals don't
            // overwrite the sync state because nothing actually ran; the
            // last successful sync's record stays the source of truth.
            if (!result.RequiresForce)
            {
                await store.UpdateSyncStatusAsync(
                    existing.Id,
                    result.SyncedAt,
                    result.Ok ? "ok" : "failed",
                    result.Sha,
                    result.Ok ? result.Message : result.Error,
                    ct
                );
            }

            return TypedResults.Ok(new ApiResult<WorkspaceSyncResult>(result));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<WorkspaceSyncResult> SyncGitAsync(
        Workspace ws,
        SecretsService secrets,
        WorkspaceFilesystemService fs,
        TimeProvider time,
        Stopwatch sw,
        bool force,
        CancellationToken ct
    )
    {
        var settings = ParseGitSettings(ws.Settings);
        if (settings is null)
            return Failure(ws.Slug, sw, time, "Workspace settings are missing or unreadable.");

        if (string.IsNullOrWhiteSpace(settings.RepositoryUrl))
            return Failure(ws.Slug, sw, time, "Repository URL is not set.");

        // Resolve the credential up-front so both clone and fetch paths use
        // the same auth machinery. None mode → null (public repo).
        string? credential = null;
        if (settings.AuthMode != GitAuthMode.None)
        {
            if (!string.IsNullOrWhiteSpace(settings.AuthSecret))
                credential = await secrets.ReadInternalAsync(settings.AuthSecret, ct);
            if (string.IsNullOrWhiteSpace(credential))
                return Failure(
                    ws.Slug,
                    sw,
                    time,
                    settings.AuthMode == GitAuthMode.HttpsPat
                        ? "PAT secret is missing — re-edit the workspace and supply the credential."
                        : "Private key secret is missing — re-edit the workspace and supply the credential."
                );
        }

        var workingTree = fs.GetWorkingTreePath(ws.Slug);
        var workingBranch = string.IsNullOrWhiteSpace(settings.WorkingBranch)
            ? "creuser/main"
            : settings.WorkingBranch;
        var sourceBranch = string.IsNullOrWhiteSpace(settings.SourceBranch)
            ? "main"
            : settings.SourceBranch;

        // SSH key is staged to a chmod-600 temp file and threaded through
        // GIT_SSH_COMMAND. PAT is supplied via http.extraHeader Basic auth,
        // which works for every Git host worth supporting (GitHub, GitLab,
        // Bitbucket, Azure DevOps, Gitea).
        string? sshKeyPath = null;
        try
        {
            var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
            var configArgs = new List<string>();

            if (settings.AuthMode == GitAuthMode.SshKey)
            {
                sshKeyPath = Path.Combine(
                    Path.GetTempPath(),
                    $"creuser-sync-key-{Guid.NewGuid():N}.pem"
                );
                await File.WriteAllTextAsync(sshKeyPath, credential!.TrimEnd() + "\n", ct);
                if (
                    RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                )
                {
                    File.SetUnixFileMode(
                        sshKeyPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite
                    );
                }
                // LogLevel=ERROR suppresses the "Permanently added 'host' to
                // the list of known hosts" warning that StrictHostKeyChecking=no
                // would otherwise emit on every fresh connection — the key is
                // immediately discarded since UserKnownHostsFile=/dev/null.
                env["GIT_SSH_COMMAND"] =
                    $"ssh -i \"{sshKeyPath}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null "
                    + "-o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=10 -o LogLevel=ERROR";
            }
            else if (settings.AuthMode == GitAuthMode.HttpsPat)
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"git:{credential}"));
                configArgs.Add("-c");
                configArgs.Add($"http.extraHeader=Authorization: Basic {basic}");
            }

            // Init-then-fetch instead of clone — works whether the working
            // branch exists on remote or only locally. A single clone command
            // can't represent "give me <working> if it exists, otherwise
            // <source>", so we drive the remote interactions directly. Shallow
            // (--depth 1) keeps the disk footprint small for v1.
            if (!fs.WorkingTreeExists(ws.Slug))
            {
                if (Directory.Exists(workingTree))
                {
                    // Stale partial clone (e.g. from a previous failed attempt).
                    try
                    {
                        Directory.Delete(workingTree, recursive: true);
                    }
                    catch
                    {
                        // Fall through; init below will fail with a clearer error
                        // if the directory really can't be cleared.
                    }
                }
                Directory.CreateDirectory(workingTree);

                var initRes = await RunGitAsync(
                    new List<string> { "init", "--quiet" },
                    env,
                    workingTree,
                    ct
                );
                if (initRes.ExitCode != 0)
                    return Failure(ws.Slug, sw, time, ParseGitError(initRes.StdErr, "init"));

                var remoteRes = await RunGitAsync(
                    new List<string> { "remote", "add", "origin", settings.RepositoryUrl },
                    env,
                    workingTree,
                    ct
                );
                if (remoteRes.ExitCode != 0)
                    return Failure(
                        ws.Slug,
                        sw,
                        time,
                        ParseGitError(remoteRes.StdErr, "remote add")
                    );
            }
            else
            {
                // Working tree already exists. Make sure the remote URL matches
                // the (possibly edited) workspace setting — the user could have
                // rotated the URL since the last sync.
                await RunGitAsync(
                    new List<string> { "remote", "set-url", "origin", settings.RepositoryUrl },
                    env,
                    workingTree,
                    ct
                );
            }

            // Always fetch the source branch — it must exist on the remote;
            // an error here is a real auth / URL / branch-name problem.
            var fetchSourceArgs = new List<string>(configArgs)
            {
                "fetch",
                "--depth",
                "1",
                "origin",
                sourceBranch,
            };
            var fetchSource = await RunGitAsync(fetchSourceArgs, env, workingTree, ct);
            if (fetchSource.ExitCode != 0)
                return Failure(ws.Slug, sw, time, ParseGitError(fetchSource.StdErr, "fetch"));

            // Try to fetch the working branch — best-effort because a fresh
            // workspace hasn't pushed it yet. Failure here is expected and
            // means "fall back to source for the local checkout target."
            var workingOnRemote = workingBranch == sourceBranch;
            if (!workingOnRemote)
            {
                var fetchWorkingArgs = new List<string>(configArgs)
                {
                    "fetch",
                    "--depth",
                    "1",
                    "origin",
                    workingBranch,
                };
                var fetchWorking = await RunGitAsync(fetchWorkingArgs, env, workingTree, ct);
                workingOnRemote = fetchWorking.ExitCode == 0;
            }

            // Capture any local drift before mirroring: modifications,
            // additions, deletions, and untracked files. `status --porcelain`
            // gives one line per dirty path, so the count is the line count.
            // We ignore exit code: on a fresh init this can fail, in which
            // case there's nothing to discard anyway.
            var statusRes = await RunGitAsync(
                new List<string> { "status", "--porcelain" },
                env,
                workingTree,
                ct
            );
            var dirtyCount =
                statusRes.ExitCode == 0
                    ? statusRes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                    : 0;

            // Refuse to sync over uncommitted changes unless the caller
            // explicitly opted in. The SPA gates a confirmation dialog on
            // RequiresForce; CLI / API callers see the same signal and can
            // pass force=true to retry. The fetch we already did above stays
            // in object storage — wasted bandwidth, but small for shallow
            // fetches and means a confirmation retry doesn't re-fetch.
            if (dirtyCount > 0 && !force)
            {
                sw.Stop();
                return new WorkspaceSyncResult(
                    Ok: false,
                    Slug: ws.Slug,
                    Sha: null,
                    LatencyMs: sw.ElapsedMilliseconds,
                    SyncedAt: time.GetUtcNow().UtcDateTime,
                    Message: null,
                    Error: $"Working tree has {dirtyCount} uncommitted change{(dirtyCount == 1 ? string.Empty : "s")}. "
                        + "Confirm to discard.",
                    DirtyCount: dirtyCount,
                    RequiresForce: true
                );
            }

            // Mirror the working tree to the remote target. Three steps in
            // sequence because each one alone leaves a gap:
            //   1. checkout -B moves the branch pointer (creates if needed)
            //   2. reset --hard forces the index + tracked files to match
            //      target, which checkout won't do when the branch already
            //      points at target (the no-op case)
            //   3. clean -fd removes untracked files / directories so the
            //      tree is byte-for-byte the remote — no orphaned scratch
            //      files surviving across syncs. We deliberately omit -x;
            //      gitignored files (build outputs, .env scratch) are left
            //      alone since the platform shouldn't reach into them.
            var target = workingOnRemote ? $"origin/{workingBranch}" : $"origin/{sourceBranch}";
            var checkoutArgs = new List<string> { "checkout", "-B", workingBranch, target };
            var checkout = await RunGitAsync(checkoutArgs, env, workingTree, ct);
            if (checkout.ExitCode != 0)
                return Failure(ws.Slug, sw, time, ParseGitError(checkout.StdErr, "checkout"));

            var resetRes = await RunGitAsync(
                new List<string> { "reset", "--hard", target },
                env,
                workingTree,
                ct
            );
            if (resetRes.ExitCode != 0)
                return Failure(ws.Slug, sw, time, ParseGitError(resetRes.StdErr, "reset"));

            await RunGitAsync(new List<string> { "clean", "-fd" }, env, workingTree, ct);

            // Resolve the SHA we just landed on.
            var rev = await RunGitAsync(
                new List<string> { "rev-parse", "HEAD" },
                env,
                workingTree,
                ct
            );
            sw.Stop();
            var sha = rev.ExitCode == 0 ? rev.StdOut.Trim() : null;
            var shortSha = sha is null ? null : sha[..Math.Min(7, sha.Length)];
            var baseMsg = workingOnRemote
                ? (shortSha is null ? "Sync complete." : $"Synced {workingBranch} to {shortSha}.")
                : (
                    shortSha is null
                        ? $"Synced {workingBranch} from {sourceBranch} (no platform commits yet)."
                        : $"Synced {workingBranch} from {sourceBranch} to {shortSha} (no platform commits yet)."
                );
            var msg =
                dirtyCount > 0
                    ? $"{baseMsg} Discarded {dirtyCount} local change{(dirtyCount == 1 ? string.Empty : "s")}."
                    : baseMsg;
            return new WorkspaceSyncResult(
                Ok: true,
                Slug: ws.Slug,
                Sha: sha,
                LatencyMs: sw.ElapsedMilliseconds,
                SyncedAt: time.GetUtcNow().UtcDateTime,
                Message: msg,
                Error: null,
                DirtyCount: dirtyCount
            );
        }
        finally
        {
            if (sshKeyPath is not null && File.Exists(sshKeyPath))
            {
                try
                {
                    File.Delete(sshKeyPath);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    private static WorkspaceSyncResult SyncLocal(Workspace ws, TimeProvider time, Stopwatch sw)
    {
        var settings = ParseLocalSettings(ws.Settings);
        if (settings is null)
            return Failure(ws.Slug, sw, time, "Workspace settings are missing or unreadable.");
        if (string.IsNullOrWhiteSpace(settings.Path))
            return Failure(ws.Slug, sw, time, "Path is not set.");
        if (!Directory.Exists(settings.Path))
            return Failure(ws.Slug, sw, time, $"Directory does not exist: {settings.Path}.");

        sw.Stop();
        return new WorkspaceSyncResult(
            Ok: true,
            Slug: ws.Slug,
            Sha: null,
            LatencyMs: sw.ElapsedMilliseconds,
            SyncedAt: time.GetUtcNow().UtcDateTime,
            Message: $"Path is accessible ({(settings.Writable ? "read-write" : "read-only")}).",
            Error: null
        );
    }

    private static WorkspaceSyncResult Failure(
        string slug,
        Stopwatch sw,
        TimeProvider time,
        string error
    )
    {
        sw.Stop();
        return new WorkspaceSyncResult(
            Ok: false,
            Slug: slug,
            Sha: null,
            LatencyMs: sw.ElapsedMilliseconds,
            SyncedAt: time.GetUtcNow().UtcDateTime,
            Message: null,
            Error: error
        );
    }

    private sealed record GitProcessResult(int ExitCode, string StdOut, string StdErr);

    private static async Task<GitProcessResult> RunGitAsync(
        IList<string> args,
        IDictionary<string, string> env,
        string? workingDir,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workingDir is not null)
            psi.WorkingDirectory = workingDir;
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var kv in env)
            psi.Environment[kv.Key] = kv.Value;

        using var proc =
            Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        // Read stdout/stderr concurrently with WaitForExit so very chatty
        // operations (clone of a big repo) don't deadlock on the pipe buffer.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 5-minute ceiling — covers a fresh clone of a moderately-sized repo
        // over a slow link. If this trips, the repo is too large for v1's
        // shallow-clone strategy and the admin needs to know.
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }
            return new GitProcessResult(
                124,
                string.Empty,
                "git process timed out after 5 minutes."
            );
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitProcessResult(proc.ExitCode, stdout, stderr);
    }

    private static string ParseGitError(string stderr, string verb)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return $"git {verb} failed (no error message captured).";

        if (stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            return "Authentication rejected. Re-check the PAT or private key on the workspace.";
        if (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "Auth rejected. The credential isn't authorized for this repo.";
        if (stderr.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase))
            return "DNS resolution failed. Check the hostname in the URL.";
        if (
            stderr.Contains(
                "Could not read from remote repository",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return "Remote rejected the connection. Either the credential lacks access or the URL is wrong.";
        if (stderr.Contains("Repository not found", StringComparison.OrdinalIgnoreCase))
            return "Repository not found at that URL.";
        if (
            stderr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)
            || (
                stderr.Contains("Remote branch", StringComparison.OrdinalIgnoreCase)
                && stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
            )
        )
            return "The configured branch doesn't exist on the remote — check the working / source branch names on the workspace.";

        // Strip the leading "fatal: " / "error: " prefix that git emits, skip
        // benign SSH/git warning + hint lines (e.g. host-key auto-add), and
        // return the first remaining non-empty line.
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (
                line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (line.StartsWith("fatal: ", StringComparison.OrdinalIgnoreCase))
                line = line["fatal: ".Length..];
            else if (line.StartsWith("error: ", StringComparison.OrdinalIgnoreCase))
                line = line["error: ".Length..];
            return line;
        }
        return $"git {verb} failed.";
    }

    private static async Task<Ok<ApiResult<WorkspaceConnectionTestResult>>> TestConnectionCore(
        TestWorkspaceConnectionRequest request,
        SecretsService secrets,
        CancellationToken ct
    )
    {
        if (request.Type == WorkspaceType.Local)
        {
            if (request.LocalSettings is null)
                return Reply(false, 0, "Local settings missing.");
            return TestLocal(request.LocalSettings);
        }

        if (request.Type != WorkspaceType.Git)
            return Reply(false, 0, $"Test connection isn't supported for type '{request.Type}'.");

        var settings = request.GitSettings;
        if (settings is null)
            return Reply(false, 0, "Git settings missing.");

        if (!GitAuthMode.IsValid(settings.AuthMode))
            return Reply(false, 0, "Invalid auth mode.");

        var url = settings.RepositoryUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return Reply(false, 0, "Repository URL is required.");

        // Resolve credential — inline first (typical when testing during
        // create), then fall back to the persisted secret on disk (rotation
        // / re-test of an existing workspace).
        var credential = await ResolveCredentialAsync(settings, secrets, ct);
        if (settings.AuthMode != GitAuthMode.None && string.IsNullOrWhiteSpace(credential))
            return Reply(
                false,
                0,
                settings.AuthMode == GitAuthMode.HttpsPat
                    ? "Personal Access Token not provided and no saved credential found."
                    : "Private key not provided and no saved credential found."
            );

        return settings.AuthMode == GitAuthMode.SshKey
            ? await TestSshAsync(url, credential!, ct)
            : await TestHttpsAsync(url, credential, ct);
    }

    /// <summary>
    /// Local-workspace test — checks the path exists, is a directory, and
    /// is readable. When <c>Writable</c> is set, also verifies write access
    /// by creating + deleting a probe file. Latency is essentially zero
    /// (no network); the latencyMs field stays at 0 for consistency.
    /// </summary>
    private static Ok<ApiResult<WorkspaceConnectionTestResult>> TestLocal(
        LocalWorkspaceSettingsDto settings
    )
    {
        // Outer try/catch turns any unexpected throw into a useful error
        // response instead of a 500. Defensive — every individual step
        // below already has its own try/catch, but unforeseen edge cases
        // (filesystem-specific errors, malformed paths through OS calls
        // we didn't anticipate) shouldn't surface as ASP.NET's default
        // "An error occurred while processing your request" wall.
        try
        {
            var path = settings.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path))
                return Reply(false, 0, "Path is required.");

            if (!Path.IsPathRooted(path))
                return Reply(false, 0, "Path must be an absolute filesystem path.");

            // Directory.Exists swallows most errors and returns false; we
            // check explicitly so the message can distinguish "doesn't
            // exist" from "exists but unreadable".
            try
            {
                if (!Directory.Exists(path))
                    return Reply(
                        false,
                        0,
                        $"Directory does not exist: {path}. Check the mount / spelling, then retry."
                    );
            }
            catch (Exception ex)
            {
                return Reply(false, 0, $"Could not check path existence: {ex.Message}");
            }

            try
            {
                // Touch one entry to confirm read access. Materialize via
                // the enumerator so any IO error (deep WSL2 path,
                // filesystem driver issue) surfaces here rather than
                // later in the call.
                using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                enumerator.MoveNext();
            }
            catch (UnauthorizedAccessException)
            {
                return Reply(false, 0, "Path is not readable by the Creuser process.");
            }
            catch (Exception ex)
            {
                return Reply(false, 0, $"Could not enumerate directory: {ex.Message}");
            }

            if (settings.Writable)
            {
                string? probe = null;
                try
                {
                    probe = Path.Combine(path, $".creuser-write-probe-{Guid.NewGuid():N}");
                    File.WriteAllText(probe, string.Empty);
                }
                catch (Exception ex)
                {
                    return Reply(
                        false,
                        0,
                        $"Path is not writable by the Creuser process: {ex.Message}. "
                            + "Either grant write permission or untoggle 'Writable'."
                    );
                }
                finally
                {
                    if (probe is not null)
                    {
                        try
                        {
                            if (File.Exists(probe))
                                File.Delete(probe);
                        }
                        catch
                        {
                            // Best-effort; the probe filename is unique so
                            // leakage is contained.
                        }
                    }
                }
            }

            return Reply(true, 0, null);
        }
        catch (Exception ex)
        {
            return Reply(
                false,
                0,
                $"Local test failed unexpectedly: {ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    private static async Task<string?> ResolveCredentialAsync(
        GitWorkspaceSettingsDto settings,
        SecretsService secrets,
        CancellationToken ct
    )
    {
        if (settings.AuthMode == GitAuthMode.None)
            return null;
        if (!string.IsNullOrWhiteSpace(settings.AuthCredential))
            return settings.AuthCredential;
        if (!string.IsNullOrWhiteSpace(settings.AuthSecret))
            return await secrets.ReadInternalAsync(settings.AuthSecret, ct);
        return null;
    }

    private static async Task<Ok<ApiResult<WorkspaceConnectionTestResult>>> TestHttpsAsync(
        string url,
        string? credential,
        CancellationToken ct
    )
    {
        if (
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
            return Reply(
                false,
                0,
                "URL must be http:// or https:// for this auth mode. Switch to SSH key for git@... URLs."
            );

        var probeUrl = BuildSmartHttpUrl(url);
        using var req = new HttpRequestMessage(HttpMethod.Get, probeUrl);
        req.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/x-git-upload-pack-advertisement")
        );
        req.Headers.UserAgent.ParseAdd("Creuser/0.1");
        if (credential is not null)
        {
            // HTTP Basic with username "git" works for GitHub, GitLab,
            // Bitbucket, Azure DevOps, and most enterprise Git servers.
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"git:{credential}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _gitTestHttp.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            sw.Stop();

            if (resp.IsSuccessStatusCode)
                return Reply(true, sw.ElapsedMilliseconds, null);

            var detail = (int)resp.StatusCode switch
            {
                401 => "Auth rejected. The PAT may be expired, revoked, or lack repo access.",
                403 =>
                    "Access forbidden. The PAT may lack the required scope (typically `repo` on GitHub).",
                404 =>
                    "Repository not found at that URL. Check spelling and that the PAT can see private repos.",
                _ => $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
            };
            return Reply(false, sw.ElapsedMilliseconds, detail);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return Reply(
                false,
                sw.ElapsedMilliseconds,
                "Connection timed out (10s). Check the URL and that the server is reachable from this host."
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Reply(false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// Real SSH round-trip — writes the private key to a chmod-600 temp
    /// file, points <c>GIT_SSH_COMMAND</c> at it, runs <c>git ls-remote</c>.
    /// Same code path the production git operations will use, so what
    /// passes here is what'll work in production.
    ///
    /// Requires the <c>git</c> and <c>ssh</c> binaries on the host. The
    /// production Dockerfile installs both; missing-binary errors surface
    /// to the admin.
    /// </summary>
    private static async Task<Ok<ApiResult<WorkspaceConnectionTestResult>>> TestSshAsync(
        string url,
        string keyContent,
        CancellationToken ct
    )
    {
        string? keyPath = null;
        var sw = Stopwatch.StartNew();
        try
        {
            keyPath = Path.Combine(Path.GetTempPath(), $"creuser-sshkey-{Guid.NewGuid():N}.pem");
            // Ensure trailing newline — ssh-keygen and openssh both expect it.
            await File.WriteAllTextAsync(keyPath, keyContent.TrimEnd() + "\n", ct);
            if (
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            )
            {
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "ls-remote", "--exit-code", url, "HEAD" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Fully non-interactive SSH: skip host-key verification (test
            // context, not a sustained connection), don't try other keys,
            // fail fast on passphrase prompts, 10-second connect timeout.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_SSH_COMMAND"] =
                $"ssh -i \"{keyPath}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null "
                + "-o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=10 -o LogLevel=ERROR";

            using var proc = Process.Start(psi);
            if (proc is null)
                return Reply(false, 0, "Failed to start git process.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }
                sw.Stop();
                return Reply(
                    false,
                    sw.ElapsedMilliseconds,
                    "SSH test timed out after 20s. The host may be unreachable or accepting connections too slowly."
                );
            }

            sw.Stop();

            if (proc.ExitCode == 0)
                return Reply(true, sw.ElapsedMilliseconds, null);

            var stderr = (await proc.StandardError.ReadToEndAsync(ct)).Trim();
            return Reply(false, sw.ElapsedMilliseconds, ParseSshError(stderr));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // ENOENT — git binary not on PATH. Production Docker images
            // install it; this is most likely a misconfigured local dev box.
            sw.Stop();
            return Reply(
                false,
                sw.ElapsedMilliseconds,
                "git binary not found on PATH. SSH connection testing requires `git` and `ssh` on the host."
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Reply(false, sw.ElapsedMilliseconds, ex.Message);
        }
        finally
        {
            if (keyPath is not null && File.Exists(keyPath))
            {
                try
                {
                    File.Delete(keyPath);
                }
                catch
                {
                    // best-effort cleanup; the temp file is chmod 600
                    // already so leakage is contained, but warn loud in logs
                    // would be nice (skipping for v1).
                }
            }
        }
    }

    /// <summary>
    /// Translate common `git ls-remote` SSH failure modes to actionable
    /// admin-facing messages. Anything unmatched falls back to the first
    /// line of stderr verbatim.
    /// </summary>
    private static string ParseSshError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "Test failed (no error message captured).";

        if (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "Auth rejected. Either the key isn't authorized for this repo, or the key has a passphrase (BatchMode prevents prompting). Use a passphraseless key.";
        if (stderr.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
            return "DNS resolution failed. Check the hostname in the URL.";
        if (stderr.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return "Connection refused. SSH may not be running on the remote, or a firewall is blocking port 22.";
        if (stderr.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase))
            return "Connection timed out. The host may not be reachable from this machine.";
        if (
            stderr.Contains("Repository not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
        )
            return "Repository not found. The path after the host may be wrong, or the key lacks access.";
        if (stderr.Contains("Load key", StringComparison.OrdinalIgnoreCase))
            return "SSH couldn't parse the private key. Confirm it's an OpenSSH-format key (starts with `-----BEGIN OPENSSH PRIVATE KEY-----`).";

        // Fall back to the first non-empty line.
        var lines = stderr.Split('\n');
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length > 0)
                return t;
        }
        return stderr;
    }

    /// <summary>
    /// Move an inline credential (PAT text or private-key text) to disk via
    /// <see cref="SecretsService"/> and return the canonical filename to
    /// store in <c>AuthSecret</c>. No-op for <c>none</c> mode or empty
    /// credential.
    /// </summary>
    private static async Task<string?> PersistInlineCredentialAsync(
        string slug,
        string authMode,
        string? credential,
        SecretsService secrets,
        CancellationToken ct
    )
    {
        if (authMode == GitAuthMode.None || string.IsNullOrWhiteSpace(credential))
            return null;

        var ext = authMode switch
        {
            GitAuthMode.HttpsPat => "pat",
            GitAuthMode.SshKey => "key",
            _ => null,
        };
        if (ext is null)
            return null;

        var filename = $"workspace-{slug}.{ext}";
        await secrets.SetAsync(filename, credential, ct);
        return filename;
    }

    private static GitWorkspaceSettings? ParseGitSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GitWorkspaceSettings>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildSmartHttpUrl(string repoUrl)
    {
        var trimmed = repoUrl.TrimEnd('/');
        if (!trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed += ".git";
        return $"{trimmed}/info/refs?service=git-upload-pack";
    }

    private static Ok<ApiResult<WorkspaceConnectionTestResult>> Reply(
        bool ok,
        long latencyMs,
        string? error
    ) =>
        TypedResults.Ok(
            new ApiResult<WorkspaceConnectionTestResult>(
                new WorkspaceConnectionTestResult(ok, latencyMs, error)
            )
        );

    private static LocalWorkspaceSettings? ParseLocalSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<LocalWorkspaceSettings>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WorkspaceResult ToResult(Workspace w, SecretsService secrets)
    {
        GitWorkspaceSettingsDto? gitSettings = null;
        LocalWorkspaceSettingsDto? localSettings = null;
        var authSecretPresent = false;

        if (w.Type == WorkspaceType.Git)
        {
            var parsed = ParseGitSettings(w.Settings);
            if (parsed is not null)
            {
                gitSettings = new GitWorkspaceSettingsDto(
                    RepositoryUrl: parsed.RepositoryUrl,
                    AuthMode: parsed.AuthMode,
                    AuthSecret: parsed.AuthSecret,
                    AuthCredential: null, // Never echo back the credential value.
                    WorkingBranch: parsed.WorkingBranch,
                    SourceBranch: parsed.SourceBranch,
                    Mode: parsed.Mode,
                    PushFrequency: parsed.PushFrequency
                );
                authSecretPresent =
                    !string.IsNullOrWhiteSpace(parsed.AuthSecret)
                    && secrets.Exists(parsed.AuthSecret);
            }
        }
        else if (w.Type == WorkspaceType.Local)
        {
            var parsed = ParseLocalSettings(w.Settings);
            if (parsed is not null)
                localSettings = new LocalWorkspaceSettingsDto(
                    Path: parsed.Path,
                    Writable: parsed.Writable
                );
        }

        return new WorkspaceResult(
            WorkspaceId: w.Id,
            Slug: w.Slug,
            Name: w.Name,
            Description: w.Description,
            Type: w.Type,
            GitSettings: gitSettings,
            LocalSettings: localSettings,
            AuthSecretPresent: authSecretPresent,
            CreatedAt: w.CreatedAt,
            UpdatedAt: w.UpdatedAt,
            LastSyncAt: w.LastSyncAt,
            LastSyncSha: w.LastSyncSha,
            LastSyncStatus: w.LastSyncStatus,
            LastSyncMessage: w.LastSyncMessage
        );
    }
}
