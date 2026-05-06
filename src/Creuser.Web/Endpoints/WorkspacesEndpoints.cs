using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Repositories;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Contracts.Responses;
using Creuser.Web.Environment;
using Creuser.Web.Validation;
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

    // Per-slug semaphore so concurrent sync OR push requests for the same
    // workspace serialize (one git operation per slug at a time), but
    // different slugs run in parallel. Push and sync share the same lock
    // because they both touch the working tree — running them concurrently
    // would risk a sync's `reset --hard` racing a push's working-branch
    // resolution, or two pushes producing duplicate uploads. In-memory
    // only — multi-instance deployments would need a Postgres advisory
    // lock here. Single-tenant on-prem v1 is fine.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        SemaphoreSlim
    > _syncLocks = new();

    public static IEndpointRouteBuilder MapWorkspacesEndpoints(this IEndpointRouteBuilder app)
    {
        // Group requires authentication; per-route auth narrows to admin
        // for mutations + admin-only endpoints (Create / Test / plugin
        // mgmt). Read endpoints (List, Get) gate on workspace membership
        // via WorkspaceAccess inside the handler.
        var group = app.MapGroup("/api/workspaces").WithTags("Workspaces").RequireAuthorization();

        group.MapGet("/", (Delegate)List).WithName("ListWorkspaces");
        group.MapGet("/{slug}", (Delegate)GetBySlug).WithName("GetWorkspace");
        group
            .MapPost("/", (Delegate)Create)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("CreateWorkspace");
        group
            .MapPut("/{slug}", (Delegate)Update)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("UpdateWorkspace");
        group
            .MapDelete("/{slug}", (Delegate)Delete)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("DeleteWorkspace");
        group
            .MapPost("/test", (Delegate)TestConnection)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("TestWorkspaceConnection");
        group
            .MapPost("/{slug}/sync", (Delegate)Sync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("SyncWorkspace");
        group
            .MapPost("/{slug}/push", (Delegate)Push)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("PushWorkspace");
        group
            .MapPost("/{slug}/commit", (Delegate)Commit)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("CommitWorkspace");
        group.MapGet("/{slug}/status", (Delegate)GetStatus).WithName("GetWorkspaceStatus");
        group
            .MapPost("/{slug}/changes", (Delegate)Changes)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("ApplyWorkspaceChanges");
        group
            .MapGet("/{slug}/files", (Delegate)GetFile)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("GetWorkspaceFile");
        group
            .MapGet("/{slug}/files/list", (Delegate)ListFolder)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("ListWorkspaceFolder");
        group.MapGet("/{slug}/plugins", (Delegate)ListPlugins).WithName("ListWorkspacePlugins");
        group
            .MapPut("/{slug}/plugins/{pluginId}", (Delegate)SetPluginEnabled)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("SetWorkspacePluginEnabled");
        group
            .MapGet("/{slug}/plugins/{pluginId}/settings", (Delegate)GetPluginSettings)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("GetWorkspacePluginSettings");
        group
            .MapPut("/{slug}/plugins/{pluginId}/settings", (Delegate)SetPluginSettings)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("SetWorkspacePluginSettings");
        group
            .MapDelete("/{slug}/plugins/{pluginId}/settings", (Delegate)DeletePluginSettings)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("DeleteWorkspacePluginSettings");

        return app;
    }

    [AiCapability(
        "workspaces.list",
        "workspaces",
        "Configured workspaces",
        "Browse the list of git repositories / S3 buckets / local paths the platform is connected to. Each workspace is the operational target for jobs, agents, and dashboards.",
        "list workspaces",
        "show workspaces",
        "what repos",
        "what is connected",
        "connected repositories",
        Route = "/settings/workspaces",
        RequiresRole = Roles.Admin
    )]
    private static async Task<Ok<ApiResult<IReadOnlyList<WorkspaceResult>>>> List(
        IWorkspaceStore store,
        IWorkspaceMemberStore members,
        SecretsService secrets,
        HttpContext http,
        int? skip,
        int? take,
        CancellationToken ct
    )
    {
        var rows = await WorkspaceAccess.ListAccessibleAsync(
            http,
            store,
            members,
            Math.Max(0, skip ?? 0),
            Math.Clamp(take ?? 50, 1, 200),
            ct
        );
        IReadOnlyList<WorkspaceResult> result = rows.Select(w => ToResult(w, secrets)).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<WorkspaceResult>>(result));
    }

    private static async Task<Results<Ok<ApiResult<WorkspaceResult>>, ProblemHttpResult>> GetBySlug(
        string slug,
        IWorkspaceStore store,
        IWorkspaceMemberStore members,
        SecretsService secrets,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, store, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        return TypedResults.Ok(new ApiResult<WorkspaceResult>(ToResult(access.Workspace, secrets)));
    }

    [AiCapability(
        "workspaces.connect",
        "workspaces",
        "Connect a new workspace",
        "Add a new git repository or local-path connection. Configure the URL/path, working branch (default `creuser/main`), source branch to sync from, and push mode (direct push vs pull request).",
        "add workspace",
        "connect repo",
        "connect repository",
        "new workspace",
        "add repo",
        "configure repo",
        "configure git",
        "add local path",
        Route = "/settings/workspaces",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<WorkspaceResult>>, ProblemHttpResult>> Create(
        CreateWorkspaceRequest request,
        IValidator<CreateWorkspaceRequest> validator,
        IWorkspaceStore store,
        SecretsService secrets,
        IDashboardSeeder dashboardSeeder,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
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

        // Seed default dashboards in a fresh DI scope so this fires-and-forgets
        // without coupling to the request scope's lifetime. Failures are logged
        // but don't fail the workspace create — empty-dashboard workspaces
        // still render cleanly via the manual "Create dashboard" flow.
        var seedScopeFactory = scopeFactory;
        var seedLogger = loggerFactory.CreateLogger("DashboardSeed");
        var workspaceId = workspace.Id;
        var creatorId = workspace.CreatedBy;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var scope = seedScopeFactory.CreateScope();
                    var seeder = scope.ServiceProvider.GetRequiredService<IDashboardSeeder>();
                    await seeder.SeedDefaultsAsync(workspaceId, creatorId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    seedLogger.LogError(
                        ex,
                        "Failed to seed default dashboards for workspace {WorkspaceId}",
                        workspaceId
                    );
                }
            },
            CancellationToken.None
        );

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

    private sealed class PushMarker { }

    [AiCapability(
        "workspaces.plugins",
        "workspaces",
        "Workspace plugins",
        "Browse plugins loaded by the platform and toggle which ones are active for this workspace. The plugin loader reads `/data/plugins/*.dll` once at startup; per-workspace toggles gate which contributions show up in this workspace's job runner picker, widget palette, agent provider list, and capability registry.",
        "manage plugins",
        "enable plugins",
        "disable plugin",
        "workspace plugins",
        "what plugins",
        Route = "/w/:slug/settings/plugins",
        RequiresRole = Roles.Admin
    )]
    private static async Task<
        Results<Ok<ApiResult<WorkspacePluginsResult>>, ProblemHttpResult>
    > ListPlugins(
        string slug,
        IWorkspaceStore store,
        Creuser.Core.Repositories.IPluginRegistry registry,
        Creuser.Core.Repositories.IWorkspacePluginStore enablement
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);

        var loaded = registry.All;
        var perWorkspace = await enablement.ListEnablementAsync(existing.Id);
        var infos = loaded
            .Select(p => new WorkspacePluginInfo(
                PluginId: p.Manifest.Id,
                Name: p.Manifest.Name,
                Version: p.Manifest.Version,
                Author: p.Manifest.Author,
                Description: p.Manifest.Description,
                Enabled: perWorkspace.GetValueOrDefault(p.Manifest.Id, false),
                Status: p.Status,
                StatusMessage: p.StatusMessage,
                Provides: p.Manifest.Provides ?? Array.Empty<string>(),
                RequiredTools: p.Manifest.RequiredTools ?? Array.Empty<string>(),
                LoadedAt: p.LoadedAt
            ))
            .ToList();
        var note =
            infos.Count == 0
                ? "No plugins discovered. Drop a Creuser plugin folder under `<dataDir>/plugins/<plugin-id>/` "
                    + "and restart the platform. Loaded plugins will appear here for the workspace admin to enable."
                : null;
        return TypedResults.Ok(
            new ApiResult<WorkspacePluginsResult>(new WorkspacePluginsResult(infos, note))
        );
    }

    [AiCapability(
        "workspaces.plugins.toggle",
        "workspaces",
        "Enable or disable a plugin for a workspace",
        "Toggle whether a host-loaded plugin's contributions (step runners, capability providers, tool registries) are visible to this workspace. Plugins are loaded process-wide at host startup; the per-workspace toggle is the gate. Admin-only.",
        "enable plugin",
        "disable plugin",
        "turn off plugin for workspace",
        Route = "/w/:slug/settings/plugins",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<
        Results<Ok<ApiResult<WorkspacePluginInfo>>, ProblemHttpResult>
    > SetPluginEnabled(
        string slug,
        string pluginId,
        SetPluginEnabledRequest request,
        IWorkspaceStore store,
        Creuser.Core.Repositories.IPluginRegistry registry,
        Creuser.Core.Repositories.IWorkspacePluginStore enablement,
        HttpContext http
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);

        var plugin = registry.Find(pluginId);
        if (plugin is null)
            return Problems.NotFound($"Plugin '{pluginId}' is not loaded.");

        await enablement.SetEnabledAsync(
            existing.Id,
            pluginId,
            request.Enabled,
            CookieAuthHelpers.GetUserId(http)
        );

        return TypedResults.Ok(
            new ApiResult<WorkspacePluginInfo>(
                new WorkspacePluginInfo(
                    PluginId: plugin.Manifest.Id,
                    Name: plugin.Manifest.Name,
                    Version: plugin.Manifest.Version,
                    Author: plugin.Manifest.Author,
                    Description: plugin.Manifest.Description,
                    Enabled: request.Enabled,
                    Status: plugin.Status,
                    StatusMessage: plugin.StatusMessage,
                    Provides: plugin.Manifest.Provides ?? Array.Empty<string>(),
                    RequiredTools: plugin.Manifest.RequiredTools ?? Array.Empty<string>(),
                    LoadedAt: plugin.LoadedAt
                )
            )
        );
    }

    public sealed record SetPluginEnabledRequest(bool Enabled);

    [AiCapability(
        "workspaces.plugins.settings.get",
        "workspaces",
        "Read plugin settings for a workspace",
        "Return the JSON settings the workspace has configured for a plugin (e.g. Slack webhook secret name + default channel, GitHub PAT secret name + default repo). Plugin authors define their own settings shape; the host stores the JSON verbatim. Returns the raw JSON or an empty object if no row exists.",
        "show plugin settings",
        "what is the plugin configured with",
        "plugin settings",
        Route = "/w/:slug/settings/plugins",
        RequiresRole = Roles.Admin
    )]
    private static async Task<
        Results<Ok<ApiResult<WorkspacePluginSettingsResult>>, ProblemHttpResult>
    > GetPluginSettings(
        string slug,
        string pluginId,
        IWorkspaceStore store,
        IPluginRegistry registry,
        IPluginSettingsStore settings
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        if (registry.Find(pluginId) is null)
            return Problems.NotFound($"Plugin '{pluginId}' is not loaded.");
        var json = await settings.GetAsync(existing.Id, pluginId);
        return TypedResults.Ok(
            new ApiResult<WorkspacePluginSettingsResult>(
                new WorkspacePluginSettingsResult(pluginId, json ?? "{}")
            )
        );
    }

    [AiCapability(
        "workspaces.plugins.settings.set",
        "workspaces",
        "Save plugin settings for a workspace",
        "Upsert the workspace's JSON settings for a plugin. The body is a `{ \"settings\": <object> }` payload — the host validates only that it parses as JSON; per-plugin shape is the plugin's responsibility. Secrets do NOT belong in this payload — store secret filenames here and the value in /data/secrets/<filename>.",
        "save plugin settings",
        "configure plugin",
        "set plugin webhook",
        Route = "/w/:slug/settings/plugins",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<
        Results<Ok<ApiResult<WorkspacePluginSettingsResult>>, ProblemHttpResult>
    > SetPluginSettings(
        string slug,
        string pluginId,
        SetPluginSettingsRequest request,
        IWorkspaceStore store,
        IPluginRegistry registry,
        IPluginSettingsStore settings,
        HttpContext http
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        if (registry.Find(pluginId) is null)
            return Problems.NotFound($"Plugin '{pluginId}' is not loaded.");

        // Settings is a free-form JSON object the plugin defined the shape
        // of. We re-serialize the parsed JsonElement so what we persist is
        // canonical JSON (no extraneous whitespace, no BOM); validating it
        // round-trips also catches obviously malformed payloads early.
        string json;
        try
        {
            json = JsonSerializer.Serialize(request.Settings);
        }
        catch (Exception ex)
        {
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["settings"] = new[] { $"Settings must be a JSON object: {ex.Message}" },
                }
            );
        }

        await settings.SetAsync(existing.Id, pluginId, json, CookieAuthHelpers.GetUserId(http));
        return TypedResults.Ok(
            new ApiResult<WorkspacePluginSettingsResult>(
                new WorkspacePluginSettingsResult(pluginId, json)
            )
        );
    }

    [AiCapability(
        "workspaces.plugins.settings.delete",
        "workspaces",
        "Reset plugin settings for a workspace",
        "Delete the saved settings row for a plugin so the plugin reverts to its built-in defaults on next read. Secrets stored under /data/secrets/ are NOT touched — operators delete those separately.",
        "reset plugin settings",
        "clear plugin config",
        Route = "/w/:slug/settings/plugins",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> DeletePluginSettings(
        string slug,
        string pluginId,
        IWorkspaceStore store,
        IPluginRegistry registry,
        IPluginSettingsStore settings
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        if (registry.Find(pluginId) is null)
            return Problems.NotFound($"Plugin '{pluginId}' is not loaded.");
        await settings.DeleteAsync(existing.Id, pluginId);
        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    public sealed record SetPluginSettingsRequest(JsonElement Settings);

    public sealed record WorkspacePluginSettingsResult(string PluginId, string SettingsJson);

    /// <summary>
    /// Sync the workspace's content. Provider-dispatched: git workspaces
    /// fetch + reset --hard, local workspaces verify the path is still
    /// readable, future providers do whatever they do. Per-slug
    /// serialization via in-memory semaphore — concurrent calls for the
    /// same slug queue, different slugs run in parallel. Side effects
    /// (sync-triggered schedule dispatch + projection-sync continuation)
    /// fire fire-and-forget after a successful sync.
    /// </summary>
    private static async Task<Results<Ok<ApiResult<WorkspaceSyncResult>>, ProblemHttpResult>> Sync(
        string slug,
        IWorkspaceStore store,
        IWorkspaceProviderRegistry registry,
        IWorkspaceStatusBroadcaster broadcaster,
        TimeProvider time,
        ILogger<SyncMarker> logger,
        Creuser.Core.Execution.IScheduleStore scheduleStore,
        Creuser.Web.Schedules.IJobScheduleDispatcher dispatcher,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct,
        // Two-phase confirmation: a first call with force=false returns
        // RequiresForce=true (and the dirty + ahead counts) when the working
        // tree is dirty or the working branch has unpushed commits. The SPA
        // confirms with the admin and retries with force=true to discard.
        bool force = false
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        var provider = registry.Resolve(existing);
        if (!provider.Capabilities.CanSync)
            return Problems.WorkspaceCapabilityNotSupported(slug, "sync");

        var gate = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            SyncOutcome outcome;
            try
            {
                outcome = await provider.SyncAsync(existing, force, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workspace sync failed for {Slug}", slug);
                outcome = new SyncOutcome(
                    Ok: false,
                    Sha: null,
                    DirtyCount: 0,
                    AheadCount: 0,
                    RequiresForce: false,
                    Message: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}",
                    LatencyMs: 0,
                    At: time.GetUtcNow().UtcDateTime
                );
            }

            var result = new WorkspaceSyncResult(
                Ok: outcome.Ok,
                Slug: slug,
                Sha: outcome.Sha,
                LatencyMs: outcome.LatencyMs,
                SyncedAt: outcome.At,
                Message: outcome.Message,
                Error: outcome.Error,
                DirtyCount: outcome.DirtyCount,
                AheadCount: outcome.AheadCount,
                RequiresForce: outcome.RequiresForce
            );

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

            // Broadcast fresh status so the SPA's header reflects new
            // counts (uncommitted, unpushed) without polling. Failure here
            // is non-fatal — the request still succeeds, the SPA just
            // misses the push and re-syncs on next interaction.
            if (!result.RequiresForce)
            {
                _ = BroadcastStatusAsync(provider, broadcaster, existing, logger);
            }

            // Fire any sync-triggered schedules for this workspace. Inline
            // dispatch (fire-and-forget) so the sync API responds quickly;
            // the dispatcher creates its own scope per job so the request
            // scope can dispose normally. Don't fire if the sync itself
            // failed — sync schedules are meant to react to a successful
            // pull.
            if (result.Ok && !result.RequiresForce)
            {
                try
                {
                    var syncSchedules = await scheduleStore.ListSyncTriggeredAsync(existing.Id, ct);
                    foreach (var schedule in syncSchedules)
                    {
                        var s = schedule;
                        _ = Task.Run(
                            async () =>
                                await dispatcher.DispatchAsync(s, "sync", CancellationToken.None),
                            CancellationToken.None
                        );
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to dispatch sync-triggered schedules for {Slug}",
                        slug
                    );
                }

                // Run the projection sync as a fire-and-forget continuation.
                // Fresh DI scope per dispatch so the request scope's
                // disposal doesn't tear down our dependencies. The
                // projection sync is idempotent and full-rebuild — running
                // it on every successful sync is the entire design.
                var workspaceForProjection = existing;
                _ = Task.Run(
                    async () =>
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var sp = scope.ServiceProvider;
                        var projectionSync =
                            sp.GetRequiredService<Creuser.Core.Projections.IProjectionSyncService>();
                        var workingTree =
                            sp.GetRequiredService<Creuser.Core.Execution.IWorkspaceWorkingTree>();
                        var projectionLogger = sp.GetRequiredService<ILogger<SyncMarker>>();
                        try
                        {
                            var path = await workingTree.ResolvePathAsync(
                                workspaceForProjection,
                                CancellationToken.None
                            );
                            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                                return;
                            var report = await projectionSync.RunAsync(
                                workspaceForProjection,
                                path,
                                CancellationToken.None
                            );
                            projectionLogger.LogInformation(
                                "Projection sync for {Slug}: {Total} entities ({Resolved} refs resolved, {Unresolved} unresolved) in {Ms}ms",
                                workspaceForProjection.Slug,
                                report.EntityTotal,
                                report.RefsResolved,
                                report.RefsUnresolved,
                                report.ScanDurationMs
                            );
                        }
                        catch (Exception ex)
                        {
                            projectionLogger.LogError(
                                ex,
                                "Projection sync failed for {Slug} after workspace sync",
                                workspaceForProjection.Slug
                            );
                        }
                    },
                    CancellationToken.None
                );
            }

            return TypedResults.Ok(new ApiResult<WorkspaceSyncResult>(result));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Push the working branch to <c>origin</c>. On-demand counterpart to
    /// <see cref="Sync"/>. The platform writes by name to
    /// <see cref="GitWorkspaceSettings.WorkingBranch"/> only — never to
    /// <see cref="GitWorkspaceSettings.SourceBranch"/> as such; if an admin
    /// has set those equal, the same code path targets that branch by name
    /// because it's what they configured. First push auto-creates the
    /// remote ref. Per-slug serialization shares the sync semaphore so
    /// push doesn't race a concurrent <see cref="Sync"/>'s
    /// <c>reset --hard</c>.
    /// </summary>
    private static async Task<Results<Ok<ApiResult<WorkspacePushResult>>, ProblemHttpResult>> Push(
        string slug,
        IWorkspaceStore store,
        IWorkspaceProviderRegistry registry,
        IWorkspaceStatusBroadcaster broadcaster,
        TimeProvider time,
        ILogger<PushMarker> logger,
        CancellationToken ct
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        var provider = registry.Resolve(existing);
        if (!provider.Capabilities.CanPush)
            return Problems.WorkspaceCapabilityNotSupported(slug, "push");

        var gate = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            PushOutcome outcome;
            try
            {
                outcome = await provider.PushAsync(existing, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workspace push failed for {Slug}", slug);
                outcome = new PushOutcome(
                    Ok: false,
                    Sha: null,
                    CommitsPushed: 0,
                    NothingToPush: false,
                    Message: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}",
                    LatencyMs: 0,
                    At: time.GetUtcNow().UtcDateTime
                );
            }

            var result = new WorkspacePushResult(
                Ok: outcome.Ok,
                Slug: slug,
                Sha: outcome.Sha,
                LatencyMs: outcome.LatencyMs,
                PushedAt: outcome.At,
                Message: outcome.Message,
                Error: outcome.Error,
                AheadCount: outcome.CommitsPushed,
                NothingToPush: outcome.NothingToPush
            );

            var status = result.Ok ? (result.NothingToPush ? "nothing-to-push" : "ok") : "failed";
            await store.UpdatePushStatusAsync(
                existing.Id,
                result.PushedAt,
                status,
                result.Sha,
                result.Ok ? result.Message : result.Error,
                ct
            );

            // Push changes the unpushed-commit count — broadcast so the
            // header's Push badge updates in real time.
            _ = BroadcastStatusAsync(provider, broadcaster, existing, logger);

            return TypedResults.Ok(new ApiResult<WorkspacePushResult>(result));
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class ChangesMarker { }

    private sealed class CommitMarker { }

    private sealed class StatusMarker { }

    /// <summary>
    /// Apply a batch of file mutations to a workspace's working surface.
    /// Provider-dispatched: git workspaces write to the working tree
    /// without committing; local workspaces write directly to disk.
    /// Commit and push are <strong>separate</strong> verbs — admins
    /// batch them at their own cadence via the dedicated endpoints.
    /// Triggers projection-sync as a fire-and-forget continuation on
    /// success so the entity graph re-projects against the new content.
    /// Per-slug serialization via the sync semaphore.
    /// </summary>
    private static async Task<
        Results<Ok<ApiResult<WorkspaceChangeResult>>, ProblemHttpResult, ValidationProblem>
    > Changes(
        string slug,
        WorkspaceChangeRequest request,
        IValidator<WorkspaceChangeRequest> validator,
        IWorkspaceStore store,
        IWorkspaceProviderRegistry registry,
        IWorkspaceStatusBroadcaster broadcaster,
        TimeProvider time,
        ILogger<ChangesMarker> logger,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct
    )
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return TypedResults.ValidationProblem(validation.ToDictionary());

        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        var provider = registry.Resolve(existing);
        if (!provider.Capabilities.CanWrite)
            return Problems.WorkspaceCapabilityNotSupported(slug, "write");

        var gate = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Translate wire DTOs to the provider's neutral file-change
            // type (same shape, lives in Core so providers don't import
            // Web contracts).
            var changes = request
                .Changes.Select(c => new Creuser.Core.Repositories.WorkspaceFileChange(
                    c.Path,
                    c.Action,
                    c.Content
                ))
                .ToList();

            WriteOutcome outcome;
            try
            {
                outcome = await provider.WriteAsync(existing, changes, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workspace write failed for {Slug}", slug);
                outcome = new WriteOutcome(
                    Ok: false,
                    FilesWritten: 0,
                    Message: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}",
                    LatencyMs: 0,
                    At: time.GetUtcNow().UtcDateTime
                );
            }

            var result = new WorkspaceChangeResult(
                Ok: outcome.Ok,
                Slug: slug,
                LatencyMs: outcome.LatencyMs,
                At: outcome.At,
                Message: outcome.Message,
                Error: outcome.Error,
                FilesChanged: outcome.FilesWritten
            );

            // On successful write, fire projection-sync as a fire-and-forget
            // continuation — same shape as the post-Sync continuation —
            // and broadcast updated status so the SPA's Commit badge
            // increments in real time.
            if (result.Ok)
            {
                _ = BroadcastStatusAsync(provider, broadcaster, existing, logger);

                var workspaceForProjection = existing;
                _ = Task.Run(
                    async () =>
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var sp = scope.ServiceProvider;
                        var projectionSync =
                            sp.GetRequiredService<Creuser.Core.Projections.IProjectionSyncService>();
                        var workingTree =
                            sp.GetRequiredService<Creuser.Core.Execution.IWorkspaceWorkingTree>();
                        var projectionLogger = sp.GetRequiredService<ILogger<ChangesMarker>>();
                        try
                        {
                            var path = await workingTree.ResolvePathAsync(
                                workspaceForProjection,
                                CancellationToken.None
                            );
                            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                                return;
                            var report = await projectionSync.RunAsync(
                                workspaceForProjection,
                                path,
                                CancellationToken.None
                            );
                            projectionLogger.LogInformation(
                                "Projection sync after write for {Slug}: {Total} entities ({Resolved} refs resolved, {Unresolved} unresolved) in {Ms}ms",
                                workspaceForProjection.Slug,
                                report.EntityTotal,
                                report.RefsResolved,
                                report.RefsUnresolved,
                                report.ScanDurationMs
                            );
                        }
                        catch (Exception ex)
                        {
                            projectionLogger.LogError(
                                ex,
                                "Projection sync failed for {Slug} after write",
                                workspaceForProjection.Slug
                            );
                        }
                    },
                    CancellationToken.None
                );
            }

            return TypedResults.Ok(new ApiResult<WorkspaceChangeResult>(result));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Batch all uncommitted changes in the workspace into one commit.
    /// Capability-gated — only providers that declare <c>CanCommit</c>
    /// (git today) implement this; others return 400 with
    /// <see cref="Problems.WorkspaceCapabilityNotSupported"/>.
    /// </summary>
    private static async Task<
        Results<Ok<ApiResult<WorkspaceCommitResult>>, ProblemHttpResult, ValidationProblem>
    > Commit(
        string slug,
        WorkspaceCommitRequest request,
        IValidator<WorkspaceCommitRequest> validator,
        IWorkspaceStore store,
        IWorkspaceProviderRegistry registry,
        IWorkspaceStatusBroadcaster broadcaster,
        TimeProvider time,
        ILogger<CommitMarker> logger,
        CancellationToken ct
    )
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return TypedResults.ValidationProblem(validation.ToDictionary());

        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        var provider = registry.Resolve(existing);
        if (!provider.Capabilities.CanCommit)
            return Problems.WorkspaceCapabilityNotSupported(slug, "commit");

        var gate = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            CommitOutcome outcome;
            try
            {
                outcome = await provider.CommitAsync(existing, request.CommitMessage, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workspace commit failed for {Slug}", slug);
                outcome = new CommitOutcome(
                    Ok: false,
                    CommitSha: null,
                    FilesCommitted: 0,
                    NothingToCommit: false,
                    Message: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}",
                    LatencyMs: 0,
                    At: time.GetUtcNow().UtcDateTime
                );
            }

            var result = new WorkspaceCommitResult(
                Ok: outcome.Ok,
                Slug: slug,
                CommitSha: outcome.CommitSha,
                LatencyMs: outcome.LatencyMs,
                CommittedAt: outcome.At,
                Message: outcome.Message,
                Error: outcome.Error,
                FilesCommitted: outcome.FilesCommitted,
                NothingToCommit: outcome.NothingToCommit
            );

            // Commit changes counts: uncommitted drops, unpushed rises.
            _ = BroadcastStatusAsync(provider, broadcaster, existing, logger);

            return TypedResults.Ok(new ApiResult<WorkspaceCommitResult>(result));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Snapshot of a workspace's pending state plus its provider's
    /// capabilities. Drives the SPA's header buttons (Commit visibility +
    /// badge count, Push visibility + badge count) and is also broadcast
    /// over SignalR after every state-mutating verb so the UI updates
    /// without polling.
    /// </summary>
    private static async Task<
        Results<Ok<ApiResult<WorkspaceStatusResult>>, ProblemHttpResult>
    > GetStatus(
        string slug,
        IWorkspaceStore store,
        IWorkspaceProviderRegistry registry,
        CancellationToken ct
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);
        var provider = registry.Resolve(existing);
        var status = await provider.GetStatusAsync(existing, ct);
        var caps = provider.Capabilities;

        return TypedResults.Ok(
            new ApiResult<WorkspaceStatusResult>(
                new WorkspaceStatusResult(
                    Slug: slug,
                    Type: existing.Type,
                    Capabilities: new WorkspaceCapabilitiesDto(
                        caps.CanWrite,
                        caps.CanCommit,
                        caps.CanPush,
                        caps.CanSync
                    ),
                    UncommittedFileCount: status.UncommittedFileCount,
                    UnpushedCommitCount: status.UnpushedCommitCount,
                    WorkingRootExists: status.WorkingRootExists
                )
            )
        );
    }

    // ─── Shared helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Pull a fresh status snapshot and broadcast it to the SPA. Called
    /// after every state-mutating verb so the header surface updates
    /// without polling. Failures are logged but never propagated — a
    /// successful operation shouldn't fail because the broadcast hub is
    /// unavailable.
    /// </summary>
    private static async Task BroadcastStatusAsync(
        IWorkspaceProvider provider,
        IWorkspaceStatusBroadcaster broadcaster,
        Workspace workspace,
        ILogger logger
    )
    {
        try
        {
            var status = await provider.GetStatusAsync(workspace, CancellationToken.None);
            await broadcaster.BroadcastAsync(workspace.Slug, status, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to broadcast workspace status for {Slug}",
                workspace.Slug
            );
        }
    }

    /// <summary>
    /// Read the raw contents of a single file in a workspace's working
    /// surface. Provider-dispatched via
    /// <see cref="IWorkspaceProvider.ResolveRootAsync"/> — git workspaces
    /// resolve to the platform's managed clone, local workspaces to the
    /// operator-configured path. The convention editor and any future
    /// file-editor surface use this to populate Monaco buffers.
    /// </summary>
    private static async Task<
        Results<Ok<ApiResult<WorkspaceFileContent>>, ProblemHttpResult>
    > GetFile(
        string slug,
        string path,
        IWorkspaceStore store,
        IWorkspaceProviderRegistry registry,
        CancellationToken ct
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);

        if (!WorkspaceChangeRequestValidator.IsSafeRelativePath(path))
            return Problems.WorkspaceFilePathInvalid(slug, path);

        var provider = registry.Resolve(existing);
        var rootPath = await provider.ResolveRootAsync(existing, ct);
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            return Problems.WorkspaceFileNotFound(
                slug,
                path,
                existing.Type == WorkspaceType.Git
                    ? "Working tree doesn't exist yet — sync the workspace first to initialize it."
                    : "Workspace root path is missing or not configured."
            );

        var rootFull = Path.GetFullPath(rootPath);
        var rel = path.Replace('\\', '/');
        var abs = Path.GetFullPath(Path.Combine(rootPath, rel));
        if (!abs.StartsWith(rootFull, StringComparison.Ordinal))
            return Problems.WorkspaceFilePathInvalid(slug, path);

        if (!File.Exists(abs))
            return Problems.WorkspaceFileNotFound(slug, path, null);

        var bytes = await File.ReadAllBytesAsync(abs, ct);
        var content = System.Text.Encoding.UTF8.GetString(bytes);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        return TypedResults.Ok(
            new ApiResult<WorkspaceFileContent>(
                new WorkspaceFileContent(
                    Path: rel,
                    Content: content,
                    ContentHash: hash,
                    SizeBytes: bytes.LongLength
                )
            )
        );
    }

    /// <summary>
    /// List the immediate folders + files at a path within the workspace's
    /// working surface. Read-only counterpart to <see cref="GetFile"/> for
    /// the file-manager widget. Provider-dispatched via
    /// <see cref="IWorkspaceProvider.ResolveRootAsync"/>; empty
    /// <paramref name="path"/> means the workspace root.
    ///
    /// <para>
    /// Hard-capped at <see cref="ListFolderEntryCap"/> total entries
    /// (folders + files combined) to keep payloads bounded; the
    /// `truncated` flag tells the widget to surface a "narrow your
    /// path" hint instead of silently hiding rows.
    /// </para>
    /// </summary>
    private const int ListFolderEntryCap = 500;

    private static async Task<
        Results<Ok<ApiResult<WorkspaceFolderListing>>, ProblemHttpResult>
    > ListFolder(
        string slug,
        string? path,
        IWorkspaceStore store,
        IWorkspaceProviderRegistry registry,
        CancellationToken ct
    )
    {
        var existing = await store.FindBySlugAsync(slug);
        if (existing is null)
            return Problems.WorkspaceNotFound(slug);

        var requested = path ?? string.Empty;
        // Empty path = root; any other value gets the same safety
        // check the file-read endpoint applies.
        if (
            !string.IsNullOrEmpty(requested)
            && !WorkspaceChangeRequestValidator.IsSafeRelativePath(requested)
        )
            return Problems.WorkspaceFilePathInvalid(slug, requested);

        var provider = registry.Resolve(existing);
        var rootPath = await provider.ResolveRootAsync(existing, ct);
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            return Problems.WorkspaceFileNotFound(
                slug,
                requested,
                existing.Type == WorkspaceType.Git
                    ? "Working tree doesn't exist yet — sync the workspace first to initialize it."
                    : "Workspace root path is missing or not configured."
            );

        var rootFull = Path.GetFullPath(rootPath);
        var rel = requested.Replace('\\', '/').TrimStart('/');
        var abs = string.IsNullOrEmpty(rel)
            ? rootFull
            : Path.GetFullPath(Path.Combine(rootPath, rel));
        if (!abs.StartsWith(rootFull, StringComparison.Ordinal))
            return Problems.WorkspaceFilePathInvalid(slug, requested);

        if (!Directory.Exists(abs))
            return Problems.WorkspaceFileNotFound(slug, requested, null);

        var folders = new List<WorkspaceFolderEntry>();
        var files = new List<WorkspaceFileEntry>();
        var truncated = false;
        try
        {
            // Enumerate directories first, then files, capping the
            // combined total. Sort within each group alphabetically
            // (case-insensitive) so the widget renders a stable order
            // without per-call client-side sorting.
            var dirInfos = new DirectoryInfo(abs)
                .EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.') || d.Name == ".creuser")
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fileInfos = new DirectoryInfo(abs)
                .EnumerateFiles()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dir in dirInfos)
            {
                if (folders.Count + files.Count >= ListFolderEntryCap)
                {
                    truncated = true;
                    break;
                }
                folders.Add(
                    new WorkspaceFolderEntry(
                        Name: dir.Name,
                        Path: string.IsNullOrEmpty(rel) ? dir.Name : $"{rel}/{dir.Name}"
                    )
                );
            }
            if (!truncated)
            {
                foreach (var file in fileInfos)
                {
                    if (folders.Count + files.Count >= ListFolderEntryCap)
                    {
                        truncated = true;
                        break;
                    }
                    files.Add(
                        new WorkspaceFileEntry(
                            Name: file.Name,
                            Path: string.IsNullOrEmpty(rel) ? file.Name : $"{rel}/{file.Name}",
                            SizeBytes: file.Length,
                            ModifiedAt: file.LastWriteTimeUtc,
                            ContentKind: ClassifyContentKind(file.Name)
                        )
                    );
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Problems.WorkspaceFileNotFound(
                slug,
                requested,
                "The platform process can't read this directory."
            );
        }

        return TypedResults.Ok(
            new ApiResult<WorkspaceFolderListing>(
                new WorkspaceFolderListing(
                    Path: rel,
                    Folders: folders,
                    Files: files,
                    Truncated: truncated
                )
            )
        );
    }

    /// <summary>
    /// Classify a filename by extension into one of <c>text</c>,
    /// <c>image</c>, <c>binary</c>, <c>unknown</c>. Drives the
    /// file-manager widget's preview pane (Monaco for text, &lt;img&gt;
    /// for images, "binary file" placeholder for binary). The
    /// <c>unknown</c> bucket gets a "view as text anyway" escape hatch
    /// in the UI for files like <c>.creuser</c> config files.
    /// </summary>
    private static string ClassifyContentKind(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext))
        {
            // Bare names like LICENSE, README, Dockerfile, Makefile —
            // treat as text since they're nearly always plain text.
            return "text";
        }
        ext = ext.ToLowerInvariant();
        return ext switch
        {
            ".md"
            or ".txt"
            or ".log"
            or ".json"
            or ".jsonc"
            or ".yaml"
            or ".yml"
            or ".xml"
            or ".html"
            or ".htm"
            or ".css"
            or ".scss"
            or ".sass"
            or ".less"
            or ".js"
            or ".jsx"
            or ".ts"
            or ".tsx"
            or ".vue"
            or ".cs"
            or ".csproj"
            or ".csx"
            or ".sln"
            or ".slnx"
            or ".props"
            or ".targets"
            or ".sql"
            or ".py"
            or ".rb"
            or ".go"
            or ".rs"
            or ".java"
            or ".kt"
            or ".swift"
            or ".c"
            or ".cpp"
            or ".h"
            or ".hpp"
            or ".sh"
            or ".bash"
            or ".zsh"
            or ".ps1"
            or ".toml"
            or ".ini"
            or ".cfg"
            or ".conf"
            or ".env"
            or ".gitignore"
            or ".gitattributes"
            or ".editorconfig"
            or ".dockerfile"
            or ".makefile"
            or ".tf"
            or ".tfvars"
            or ".lock"
            or ".graphql"
            or ".gql"
            or ".proto" => "text",

            ".png"
            or ".jpg"
            or ".jpeg"
            or ".gif"
            or ".webp"
            or ".svg"
            or ".bmp"
            or ".ico"
            or ".avif" => "image",

            ".zip"
            or ".tar"
            or ".gz"
            or ".tgz"
            or ".bz2"
            or ".7z"
            or ".rar"
            or ".pdf"
            or ".dll"
            or ".exe"
            or ".so"
            or ".dylib"
            or ".bin"
            or ".woff"
            or ".woff2"
            or ".ttf"
            or ".otf"
            or ".eot"
            or ".mp3"
            or ".mp4"
            or ".wav"
            or ".flac"
            or ".ogg"
            or ".webm"
            or ".mov"
            or ".db"
            or ".sqlite" => "binary",

            _ => "unknown",
        };
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
            LastSyncMessage: w.LastSyncMessage,
            LastPushAt: w.LastPushAt,
            LastPushSha: w.LastPushSha,
            LastPushStatus: w.LastPushStatus,
            LastPushMessage: w.LastPushMessage
        );
    }
}
