using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Creuser.Auth.Core;
using Creuser.Core.Repositories;
using Creuser.Web.Environment;

namespace Creuser.Web.Workspaces;

/// <summary>
/// <see cref="IWorkspaceProvider"/> for <see cref="WorkspaceType.Git"/>.
/// Backed by a managed clone under <c>&lt;dataDir&gt;/workspaces/&lt;slug&gt;/</c>
/// (resolved via <see cref="WorkspaceFilesystemService"/>). Implements all
/// four verbs:
///   - Write: writes files to the working tree, no commit. Auto-initializes
///     the clone via <see cref="SyncAsync"/> on first write to a fresh
///     workspace, so admins don't have to bounce through the workspaces
///     page to click Sync before they can save anything.
///   - Sync: fetch + checkout + reset --hard against the resolved target
///     (working branch on remote, or source branch as fallback for the
///     first sync).
///   - Commit: stages every uncommitted change (`git add -A`) and writes
///     one commit under a fixed bot identity.
///   - Push: pushes the working branch by name; auto-creates the remote
///     ref on first push.
///
/// <para>
/// Lifts the inline git ops out of the previous <c>WorkspacesEndpoints</c>
/// (<c>SyncGitAsync</c> / <c>PushGitAsync</c> / <c>ApplyGitChangesAsync</c>)
/// so endpoints reduce to dispatch and the per-provider logic lives in
/// one place.
/// </para>
/// </summary>
public sealed class GitWorkspaceProvider : IWorkspaceProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time;
    private readonly SecretsService _secrets;
    private readonly WorkspaceFilesystemService _fs;

    public GitWorkspaceProvider(
        TimeProvider time,
        SecretsService secrets,
        WorkspaceFilesystemService fs
    )
    {
        _time = time;
        _secrets = secrets;
        _fs = fs;
    }

    public WorkspaceCapabilities Capabilities { get; } =
        new(CanWrite: true, CanCommit: true, CanPush: true, CanSync: true);

    public Task<string?> ResolveRootAsync(Workspace workspace, CancellationToken ct = default)
    {
        // Always returns the path even when the clone hasn't been
        // initialized — callers check via WorkingRootExists / Directory.Exists.
        return Task.FromResult<string?>(_fs.GetWorkingTreePath(workspace.Slug));
    }

    public async Task<WorkspaceProviderStatus> GetStatusAsync(
        Workspace workspace,
        CancellationToken ct = default
    )
    {
        if (!_fs.WorkingTreeExists(workspace.Slug))
            return new WorkspaceProviderStatus(
                UncommittedFileCount: 0,
                UnpushedCommitCount: 0,
                WorkingRootExists: false
            );

        var settings = ParseSettings(workspace);
        var workingBranch = ResolveWorkingBranch(settings);
        var workingTree = _fs.GetWorkingTreePath(workspace.Slug);
        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };

        // Uncommitted files: status --porcelain prints one line per dirty
        // path (modified, added, deleted, untracked, etc.). No network.
        var statusRes = await RunGitAsync(
            new List<string> { "status", "--porcelain" },
            env,
            workingTree,
            ct
        );
        var dirty =
            statusRes.ExitCode == 0
                ? statusRes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

        // Unpushed commits: rev-list count vs origin/<wb>. Reads local refs
        // only — no fetch — so this is fast and shows what the user has
        // done locally. Push handler does its own fetch at push time to
        // resolve the actual non-fast-forward case. If the local branch
        // doesn't exist (clone present but never checked out), count is 0.
        // If origin/<wb> doesn't exist (first push case), count includes
        // every commit on the local branch.
        var ahead = 0;
        var localRefRes = await RunGitAsync(
            new List<string> { "rev-parse", "--verify", "--quiet", $"refs/heads/{workingBranch}" },
            env,
            workingTree,
            ct
        );
        if (localRefRes.ExitCode == 0)
        {
            var remoteRefRes = await RunGitAsync(
                new List<string>
                {
                    "rev-parse",
                    "--verify",
                    "--quiet",
                    $"refs/remotes/origin/{workingBranch}",
                },
                env,
                workingTree,
                ct
            );
            var aheadArgs =
                remoteRefRes.ExitCode == 0
                    ? new List<string>
                    {
                        "rev-list",
                        "--count",
                        $"origin/{workingBranch}..{workingBranch}",
                    }
                    : new List<string> { "rev-list", "--count", workingBranch };
            var aheadRes = await RunGitAsync(aheadArgs, env, workingTree, ct);
            if (aheadRes.ExitCode == 0 && int.TryParse(aheadRes.StdOut.Trim(), out var parsed))
                ahead = parsed;
        }

        return new WorkspaceProviderStatus(
            UncommittedFileCount: dirty,
            UnpushedCommitCount: ahead,
            WorkingRootExists: true
        );
    }

    public async Task<WriteOutcome> WriteAsync(
        Workspace workspace,
        IReadOnlyList<WorkspaceFileChange> changes,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();

        // Auto-init: a freshly-created git workspace's clone doesn't exist
        // yet. Delegate to SyncAsync to do the same fetch + checkout the
        // workspaces-page Sync button does, then proceed with writes. The
        // platform should never make admins do work it can do itself.
        if (!_fs.WorkingTreeExists(workspace.Slug))
        {
            var initOutcome = await SyncAsync(workspace, force: false, ct);
            if (!initOutcome.Ok)
                return new WriteOutcome(
                    Ok: false,
                    FilesWritten: 0,
                    Message: null,
                    Error: $"Working tree didn't exist and auto-init failed: {initOutcome.Error ?? "unknown error"}",
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                );
        }

        var workingTree = _fs.GetWorkingTreePath(workspace.Slug);
        var rootFull = Path.GetFullPath(workingTree);
        foreach (var change in changes)
        {
            var rel = change.Path.Replace('\\', '/');
            var abs = Path.GetFullPath(Path.Combine(workingTree, rel));
            if (!abs.StartsWith(rootFull, StringComparison.Ordinal))
                return new WriteOutcome(
                    Ok: false,
                    FilesWritten: 0,
                    Message: null,
                    Error: $"Path '{change.Path}' resolves outside the workspace working tree.",
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                );

            if (change.Action == "write")
            {
                var dir = Path.GetDirectoryName(abs);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(abs, change.Content ?? string.Empty, ct);
            }
            else if (change.Action == "delete")
            {
                if (File.Exists(abs))
                    File.Delete(abs);
            }
            else
            {
                return new WriteOutcome(
                    Ok: false,
                    FilesWritten: 0,
                    Message: null,
                    Error: $"Unknown action '{change.Action}' for '{change.Path}'.",
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                );
            }
        }

        sw.Stop();
        var plural = changes.Count == 1 ? "file" : "files";
        return new WriteOutcome(
            Ok: true,
            FilesWritten: changes.Count,
            Message: $"Saved {changes.Count} {plural}.",
            Error: null,
            LatencyMs: sw.ElapsedMilliseconds,
            At: _time.GetUtcNow().UtcDateTime
        );
    }

    public async Task<CommitOutcome> CommitAsync(
        Workspace workspace,
        string commitMessage,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        if (!_fs.WorkingTreeExists(workspace.Slug))
            return CommitFail(sw, "Working tree doesn't exist — sync the workspace first.");

        var workingTree = _fs.GetWorkingTreePath(workspace.Slug);
        var settings = ParseSettings(workspace);
        var workingBranch = ResolveWorkingBranch(settings);
        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };

        // Stage everything currently dirty.
        var addRes = await RunGitAsync(new List<string> { "add", "-A" }, env, workingTree, ct);
        if (addRes.ExitCode != 0)
            return CommitFail(sw, ParseGitError(addRes.StdErr, "add"));

        // No-op detection: nothing staged after add means there's literally
        // nothing changed in the working tree. Caller probably called
        // /commit with a stale UI (no badge) — surface as success no-op.
        var diffRes = await RunGitAsync(
            new List<string> { "diff", "--cached", "--quiet" },
            env,
            workingTree,
            ct
        );
        if (diffRes.ExitCode == 0)
        {
            sw.Stop();
            return new CommitOutcome(
                Ok: true,
                CommitSha: null,
                FilesCommitted: 0,
                NothingToCommit: true,
                Message: "Nothing to commit — working tree is clean.",
                Error: null,
                LatencyMs: sw.ElapsedMilliseconds,
                At: _time.GetUtcNow().UtcDateTime
            );
        }

        // Count files going into this commit so the UI can report
        // "Committed N files at <sha>" instead of opaque "Committed."
        var stagedListRes = await RunGitAsync(
            new List<string> { "diff", "--cached", "--name-only" },
            env,
            workingTree,
            ct
        );
        var fileCount =
            stagedListRes.ExitCode == 0
                ? stagedListRes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

        var commitArgs = new List<string>
        {
            "-c",
            "user.name=Creuser",
            "-c",
            "user.email=noreply@creuser.local",
            "commit",
            "-m",
            commitMessage,
        };
        var commitRes = await RunGitAsync(commitArgs, env, workingTree, ct);
        if (commitRes.ExitCode != 0)
            return CommitFail(sw, ParseGitError(commitRes.StdErr, "commit"));

        var rev = await RunGitAsync(new List<string> { "rev-parse", "HEAD" }, env, workingTree, ct);
        var sha = rev.ExitCode == 0 ? rev.StdOut.Trim() : null;
        var shortSha = sha is null ? null : sha[..Math.Min(7, sha.Length)];
        var pluralFile = fileCount == 1 ? "file" : "files";

        sw.Stop();
        return new CommitOutcome(
            Ok: true,
            CommitSha: sha,
            FilesCommitted: fileCount,
            NothingToCommit: false,
            Message: shortSha is null
                ? $"Committed {fileCount} {pluralFile} on {workingBranch}."
                : $"Committed {fileCount} {pluralFile} on {workingBranch} at {shortSha}.",
            Error: null,
            LatencyMs: sw.ElapsedMilliseconds,
            At: _time.GetUtcNow().UtcDateTime
        );
    }

    public async Task<PushOutcome> PushAsync(Workspace workspace, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var settings = ParseSettings(workspace);
        if (settings is null)
            return PushFail(sw, "Workspace settings are missing or unreadable.");
        if (string.IsNullOrWhiteSpace(settings.RepositoryUrl))
            return PushFail(sw, "Repository URL is not set.");
        if (!_fs.WorkingTreeExists(workspace.Slug))
            return PushFail(
                sw,
                "Working tree doesn't exist yet — sync the workspace first to initialize it."
            );

        var auth = await ResolveAuthAsync(workspace, settings, ct);
        if (auth.Error is not null)
            return PushFail(sw, auth.Error);

        var workingTree = _fs.GetWorkingTreePath(workspace.Slug);
        var workingBranch = ResolveWorkingBranch(settings);
        try
        {
            // Make sure URL is current (admin may have rotated it).
            await RunGitAsync(
                new List<string> { "remote", "set-url", "origin", settings.RepositoryUrl },
                auth.Env,
                workingTree,
                ct
            );

            var fetchArgs = new List<string>(auth.ConfigArgs)
            {
                "fetch",
                "--depth",
                "1",
                "origin",
                workingBranch,
            };
            var fetchRes = await RunGitAsync(fetchArgs, auth.Env, workingTree, ct);
            var remoteRefExists = fetchRes.ExitCode == 0;

            var aheadArgs = remoteRefExists
                ? new List<string>
                {
                    "rev-list",
                    "--count",
                    $"origin/{workingBranch}..{workingBranch}",
                }
                : new List<string> { "rev-list", "--count", workingBranch };
            var aheadRes = await RunGitAsync(aheadArgs, auth.Env, workingTree, ct);
            var ahead = 0;
            if (aheadRes.ExitCode == 0 && int.TryParse(aheadRes.StdOut.Trim(), out var parsed))
                ahead = parsed;

            var rev = await RunGitAsync(
                new List<string> { "rev-parse", "HEAD" },
                auth.Env,
                workingTree,
                ct
            );
            var sha = rev.ExitCode == 0 ? rev.StdOut.Trim() : null;
            var shortSha = sha is null ? null : sha[..Math.Min(7, sha.Length)];

            if (ahead == 0)
            {
                sw.Stop();
                return new PushOutcome(
                    Ok: true,
                    Sha: sha,
                    CommitsPushed: 0,
                    NothingToPush: true,
                    Message: shortSha is null
                        ? $"Already up-to-date with origin/{workingBranch}."
                        : $"Already up-to-date with origin/{workingBranch} at {shortSha}.",
                    Error: null,
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                );
            }

            var pushArgs = new List<string>(auth.ConfigArgs)
            {
                "push",
                "origin",
                $"{workingBranch}:{workingBranch}",
            };
            var pushRes = await RunGitAsync(pushArgs, auth.Env, workingTree, ct);
            if (pushRes.ExitCode != 0)
            {
                var stderr = pushRes.StdErr ?? string.Empty;
                var nonFf =
                    stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                    || (
                        stderr.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                        && stderr.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                    );
                var error = nonFf
                    ? $"Remote rejected push — origin/{workingBranch} has commits we don't have. Sync the workspace and retry."
                    : ParseGitError(stderr, "push");
                sw.Stop();
                return new PushOutcome(
                    Ok: false,
                    Sha: sha,
                    CommitsPushed: 0,
                    NothingToPush: false,
                    Message: null,
                    Error: error,
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                );
            }

            sw.Stop();
            var plural = ahead == 1 ? "commit" : "commits";
            var msg = remoteRefExists
                ? (
                    shortSha is null
                        ? $"Pushed {ahead} {plural} to origin/{workingBranch}."
                        : $"Pushed {ahead} {plural} to origin/{workingBranch} (now at {shortSha})."
                )
                : (
                    shortSha is null
                        ? $"Created origin/{workingBranch} with {ahead} {plural}."
                        : $"Created origin/{workingBranch} at {shortSha} ({ahead} {plural})."
                );
            return new PushOutcome(
                Ok: true,
                Sha: sha,
                CommitsPushed: ahead,
                NothingToPush: false,
                Message: msg,
                Error: null,
                LatencyMs: sw.ElapsedMilliseconds,
                At: _time.GetUtcNow().UtcDateTime
            );
        }
        finally
        {
            auth.Cleanup?.Invoke();
        }
    }

    public async Task<SyncOutcome> SyncAsync(
        Workspace workspace,
        bool force,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        var settings = ParseSettings(workspace);
        if (settings is null)
            return SyncFail(sw, "Workspace settings are missing or unreadable.");
        if (string.IsNullOrWhiteSpace(settings.RepositoryUrl))
            return SyncFail(sw, "Repository URL is not set.");

        var auth = await ResolveAuthAsync(workspace, settings, ct);
        if (auth.Error is not null)
            return SyncFail(sw, auth.Error);

        var workingTree = _fs.GetWorkingTreePath(workspace.Slug);
        var workingBranch = ResolveWorkingBranch(settings);
        var sourceBranch = string.IsNullOrWhiteSpace(settings.SourceBranch)
            ? "main"
            : settings.SourceBranch;

        try
        {
            // Init-or-update phase. Identical structure to the previous
            // SyncGitAsync: init+remote-add when the clone is fresh,
            // remote-set-url otherwise to pick up admin's URL changes.
            if (!_fs.WorkingTreeExists(workspace.Slug))
            {
                if (Directory.Exists(workingTree))
                {
                    try
                    {
                        Directory.Delete(workingTree, recursive: true);
                    }
                    catch
                    {
                        // best-effort — init below will produce a clearer error
                    }
                }
                Directory.CreateDirectory(workingTree);

                var initRes = await RunGitAsync(
                    new List<string> { "init", "--quiet" },
                    auth.Env,
                    workingTree,
                    ct
                );
                if (initRes.ExitCode != 0)
                    return SyncFail(sw, ParseGitError(initRes.StdErr, "init"));

                var remoteAddRes = await RunGitAsync(
                    new List<string> { "remote", "add", "origin", settings.RepositoryUrl },
                    auth.Env,
                    workingTree,
                    ct
                );
                if (remoteAddRes.ExitCode != 0)
                    return SyncFail(sw, ParseGitError(remoteAddRes.StdErr, "remote add"));
            }
            else
            {
                await RunGitAsync(
                    new List<string> { "remote", "set-url", "origin", settings.RepositoryUrl },
                    auth.Env,
                    workingTree,
                    ct
                );
            }

            // Fetch source (always exists), best-effort fetch working.
            var fetchSourceArgs = new List<string>(auth.ConfigArgs)
            {
                "fetch",
                "--depth",
                "1",
                "origin",
                sourceBranch,
            };
            var fetchSource = await RunGitAsync(fetchSourceArgs, auth.Env, workingTree, ct);
            if (fetchSource.ExitCode != 0)
                return SyncFail(sw, ParseGitError(fetchSource.StdErr, "fetch"));

            var workingOnRemote = workingBranch == sourceBranch;
            if (!workingOnRemote)
            {
                var fetchWorkingArgs = new List<string>(auth.ConfigArgs)
                {
                    "fetch",
                    "--depth",
                    "1",
                    "origin",
                    workingBranch,
                };
                var fetchWorking = await RunGitAsync(fetchWorkingArgs, auth.Env, workingTree, ct);
                workingOnRemote = fetchWorking.ExitCode == 0;
            }

            var target = workingOnRemote ? $"origin/{workingBranch}" : $"origin/{sourceBranch}";

            // Dirty-tree + ahead-count gate (force=true bypasses both).
            var statusRes = await RunGitAsync(
                new List<string> { "status", "--porcelain" },
                auth.Env,
                workingTree,
                ct
            );
            var dirtyCount =
                statusRes.ExitCode == 0
                    ? statusRes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                    : 0;

            var aheadCount = 0;
            var localRefRes = await RunGitAsync(
                new List<string>
                {
                    "rev-parse",
                    "--verify",
                    "--quiet",
                    $"refs/heads/{workingBranch}",
                },
                auth.Env,
                workingTree,
                ct
            );
            if (localRefRes.ExitCode == 0)
            {
                var aheadRes = await RunGitAsync(
                    new List<string> { "rev-list", "--count", $"{target}..{workingBranch}" },
                    auth.Env,
                    workingTree,
                    ct
                );
                if (
                    aheadRes.ExitCode == 0
                    && int.TryParse(aheadRes.StdOut.Trim(), out var parsedAhead)
                )
                    aheadCount = parsedAhead;
            }

            if ((dirtyCount > 0 || aheadCount > 0) && !force)
            {
                sw.Stop();
                var parts = new List<string>(2);
                if (dirtyCount > 0)
                    parts.Add(
                        $"{dirtyCount} uncommitted change{(dirtyCount == 1 ? string.Empty : "s")}"
                    );
                if (aheadCount > 0)
                    parts.Add(
                        $"{aheadCount} unpushed commit{(aheadCount == 1 ? string.Empty : "s")}"
                    );
                return new SyncOutcome(
                    Ok: false,
                    Sha: null,
                    DirtyCount: dirtyCount,
                    AheadCount: aheadCount,
                    RequiresForce: true,
                    Message: null,
                    Error: $"Working tree has {string.Join(" and ", parts)}. Confirm to discard.",
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                );
            }

            // Mirror to target.
            var checkoutRes = await RunGitAsync(
                new List<string> { "checkout", "-B", workingBranch, target },
                auth.Env,
                workingTree,
                ct
            );
            if (checkoutRes.ExitCode != 0)
                return SyncFail(sw, ParseGitError(checkoutRes.StdErr, "checkout"));

            var resetRes = await RunGitAsync(
                new List<string> { "reset", "--hard", target },
                auth.Env,
                workingTree,
                ct
            );
            if (resetRes.ExitCode != 0)
                return SyncFail(sw, ParseGitError(resetRes.StdErr, "reset"));

            await RunGitAsync(new List<string> { "clean", "-fd" }, auth.Env, workingTree, ct);

            var rev = await RunGitAsync(
                new List<string> { "rev-parse", "HEAD" },
                auth.Env,
                workingTree,
                ct
            );
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

            sw.Stop();
            return new SyncOutcome(
                Ok: true,
                Sha: sha,
                DirtyCount: dirtyCount,
                AheadCount: aheadCount,
                RequiresForce: false,
                Message: msg,
                Error: null,
                LatencyMs: sw.ElapsedMilliseconds,
                At: _time.GetUtcNow().UtcDateTime
            );
        }
        finally
        {
            auth.Cleanup?.Invoke();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static GitWorkspaceSettings? ParseSettings(Workspace ws)
    {
        if (string.IsNullOrWhiteSpace(ws.Settings))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GitWorkspaceSettings>(ws.Settings, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveWorkingBranch(GitWorkspaceSettings? settings) =>
        string.IsNullOrWhiteSpace(settings?.WorkingBranch)
            ? "creuser/main"
            : settings.WorkingBranch;

    private async Task<AuthContext> ResolveAuthAsync(
        Workspace workspace,
        GitWorkspaceSettings settings,
        CancellationToken ct
    )
    {
        string? credential = null;
        if (settings.AuthMode != GitAuthMode.None)
        {
            if (!string.IsNullOrWhiteSpace(settings.AuthSecret))
                credential = await _secrets.ReadInternalAsync(settings.AuthSecret, ct);
            if (string.IsNullOrWhiteSpace(credential))
                return new AuthContext(
                    Env: new Dictionary<string, string>(),
                    ConfigArgs: new List<string>(),
                    Cleanup: null,
                    Error: settings.AuthMode == GitAuthMode.HttpsPat
                        ? "PAT secret is missing — re-edit the workspace and supply the credential."
                        : "Private key secret is missing — re-edit the workspace and supply the credential."
                );
        }

        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        var configArgs = new List<string>();
        Action? cleanup = null;

        if (settings.AuthMode == GitAuthMode.SshKey)
        {
            var sshKeyPath = Path.Combine(
                Path.GetTempPath(),
                $"creuser-git-key-{Guid.NewGuid():N}.pem"
            );
            await File.WriteAllTextAsync(sshKeyPath, credential!.TrimEnd() + "\n", ct);
            if (
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            )
            {
                File.SetUnixFileMode(sshKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            env["GIT_SSH_COMMAND"] =
                $"ssh -i \"{sshKeyPath}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null "
                + "-o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=10 -o LogLevel=ERROR";
            cleanup = () =>
            {
                try
                {
                    File.Delete(sshKeyPath);
                }
                catch
                {
                    // best-effort
                }
            };
        }
        else if (settings.AuthMode == GitAuthMode.HttpsPat)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"git:{credential}"));
            configArgs.Add("-c");
            configArgs.Add($"http.extraHeader=Authorization: Basic {basic}");
        }

        return new AuthContext(env, configArgs, cleanup, Error: null);
    }

    private sealed record AuthContext(
        Dictionary<string, string> Env,
        List<string> ConfigArgs,
        Action? Cleanup,
        string? Error
    );

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

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
        if (stderr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
            return $"git {verb} failed: branch not found on remote.";
        return $"git {verb} failed: {stderr.Trim()}";
    }

    private CommitOutcome CommitFail(Stopwatch sw, string error)
    {
        sw.Stop();
        return new CommitOutcome(
            Ok: false,
            CommitSha: null,
            FilesCommitted: 0,
            NothingToCommit: false,
            Message: null,
            Error: error,
            LatencyMs: sw.ElapsedMilliseconds,
            At: _time.GetUtcNow().UtcDateTime
        );
    }

    private PushOutcome PushFail(Stopwatch sw, string error)
    {
        sw.Stop();
        return new PushOutcome(
            Ok: false,
            Sha: null,
            CommitsPushed: 0,
            NothingToPush: false,
            Message: null,
            Error: error,
            LatencyMs: sw.ElapsedMilliseconds,
            At: _time.GetUtcNow().UtcDateTime
        );
    }

    private SyncOutcome SyncFail(Stopwatch sw, string error)
    {
        sw.Stop();
        return new SyncOutcome(
            Ok: false,
            Sha: null,
            DirtyCount: 0,
            AheadCount: 0,
            RequiresForce: false,
            Message: null,
            Error: error,
            LatencyMs: sw.ElapsedMilliseconds,
            At: _time.GetUtcNow().UtcDateTime
        );
    }
}
