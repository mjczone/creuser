using System.Diagnostics;
using System.Text.Json;
using Creuser.Core.Repositories;

namespace Creuser.Web.Workspaces;

/// <summary>
/// <see cref="IWorkspaceProvider"/> for <see cref="WorkspaceType.Local"/>.
/// Backed by a directory on the operator-configured filesystem path.
/// Writes go directly to disk — there is no commit boundary, no remote,
/// nothing to push. Sync degenerates to "verify the path still exists."
///
/// <para>
/// Capability matrix:
///   Write — yes (when <see cref="LocalWorkspaceSettings.Writable"/>)
///   Sync — yes (no-op verify)
///   Commit / Push — no (throws <see cref="NotSupportedException"/>;
///   capability check at the endpoint layer is the actual gate).
/// </para>
/// </summary>
public sealed class LocalWorkspaceProvider : IWorkspaceProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time;

    public LocalWorkspaceProvider(TimeProvider time)
    {
        _time = time;
    }

    public WorkspaceCapabilities Capabilities { get; } =
        new(CanWrite: true, CanCommit: false, CanPush: false, CanSync: true);

    public Task<string?> ResolveRootAsync(Workspace workspace, CancellationToken ct = default)
    {
        var settings = ParseSettings(workspace);
        return Task.FromResult<string?>(
            string.IsNullOrWhiteSpace(settings?.Path) ? null : settings.Path
        );
    }

    public Task<WorkspaceProviderStatus> GetStatusAsync(
        Workspace workspace,
        CancellationToken ct = default
    )
    {
        var settings = ParseSettings(workspace);
        var exists = !string.IsNullOrWhiteSpace(settings?.Path) && Directory.Exists(settings.Path);
        // Local has no commit boundary and no remote — both counts are
        // structurally zero. The header surface uses canCommit/canPush
        // capabilities to hide the buttons entirely; this is just the
        // honest snapshot for completeness / debugging.
        return Task.FromResult(
            new WorkspaceProviderStatus(
                UncommittedFileCount: 0,
                UnpushedCommitCount: 0,
                WorkingRootExists: exists
            )
        );
    }

    public async Task<WriteOutcome> WriteAsync(
        Workspace workspace,
        IReadOnlyList<WorkspaceFileChange> changes,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        var settings = ParseSettings(workspace);
        if (settings is null)
            return Failure(sw, "Workspace settings are missing or unreadable.");
        if (string.IsNullOrWhiteSpace(settings.Path))
            return Failure(sw, "Path is not set.");
        if (!Directory.Exists(settings.Path))
            return Failure(sw, $"Directory does not exist: {settings.Path}.");
        if (!settings.Writable)
            return Failure(
                sw,
                "Workspace is mounted read-only — flip the 'Allow writes' toggle in workspace settings to enable file writes."
            );

        var rootFull = Path.GetFullPath(settings.Path);
        foreach (var change in changes)
        {
            var rel = change.Path.Replace('\\', '/');
            var abs = Path.GetFullPath(Path.Combine(settings.Path, rel));
            if (!abs.StartsWith(rootFull, StringComparison.Ordinal))
                return Failure(sw, $"Path '{change.Path}' resolves outside the workspace root.");

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
                return Failure(sw, $"Unknown action '{change.Action}' for '{change.Path}'.");
            }
        }

        sw.Stop();
        var plural = changes.Count == 1 ? "file" : "files";
        return new WriteOutcome(
            Ok: true,
            FilesWritten: changes.Count,
            Message: $"Wrote {changes.Count} {plural} to local workspace.",
            Error: null,
            LatencyMs: sw.ElapsedMilliseconds,
            At: _time.GetUtcNow().UtcDateTime
        );
    }

    public Task<SyncOutcome> SyncAsync(
        Workspace workspace,
        bool force,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        var settings = ParseSettings(workspace);
        if (settings is null)
            return Task.FromResult(
                new SyncOutcome(
                    Ok: false,
                    Sha: null,
                    DirtyCount: 0,
                    AheadCount: 0,
                    RequiresForce: false,
                    Message: null,
                    Error: "Workspace settings are missing or unreadable.",
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                )
            );
        if (string.IsNullOrWhiteSpace(settings.Path))
            return Task.FromResult(
                new SyncOutcome(
                    Ok: false,
                    Sha: null,
                    DirtyCount: 0,
                    AheadCount: 0,
                    RequiresForce: false,
                    Message: null,
                    Error: "Path is not set.",
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                )
            );
        if (!Directory.Exists(settings.Path))
            return Task.FromResult(
                new SyncOutcome(
                    Ok: false,
                    Sha: null,
                    DirtyCount: 0,
                    AheadCount: 0,
                    RequiresForce: false,
                    Message: null,
                    Error: $"Directory does not exist: {settings.Path}.",
                    LatencyMs: sw.ElapsedMilliseconds,
                    At: _time.GetUtcNow().UtcDateTime
                )
            );

        sw.Stop();
        return Task.FromResult(
            new SyncOutcome(
                Ok: true,
                Sha: null,
                DirtyCount: 0,
                AheadCount: 0,
                RequiresForce: false,
                Message: $"Path is accessible ({(settings.Writable ? "read-write" : "read-only")}).",
                Error: null,
                LatencyMs: sw.ElapsedMilliseconds,
                At: _time.GetUtcNow().UtcDateTime
            )
        );
    }

    public Task<CommitOutcome> CommitAsync(
        Workspace workspace,
        string commitMessage,
        CancellationToken ct = default
    ) =>
        throw new NotSupportedException(
            "Local workspaces don't have a commit boundary — writes persist directly. The endpoint's capability check is the canonical gate; this throw is defense-in-depth."
        );

    public Task<PushOutcome> PushAsync(Workspace workspace, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Local workspaces have no remote to push to. The endpoint's capability check is the canonical gate; this throw is defense-in-depth."
        );

    private static LocalWorkspaceSettings? ParseSettings(Workspace ws)
    {
        if (string.IsNullOrWhiteSpace(ws.Settings))
            return null;
        try
        {
            return JsonSerializer.Deserialize<LocalWorkspaceSettings>(ws.Settings, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private WriteOutcome Failure(Stopwatch sw, string error)
    {
        sw.Stop();
        return new WriteOutcome(
            Ok: false,
            FilesWritten: 0,
            Message: null,
            Error: error,
            LatencyMs: sw.ElapsedMilliseconds,
            At: _time.GetUtcNow().UtcDateTime
        );
    }
}
