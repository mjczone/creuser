namespace Creuser.Core.Repositories;

/// <summary>
/// Per-provider abstraction for the four verbs Creuser exposes against a
/// workspace's content surface — write / commit / push / sync. Each
/// implementation declares which verbs it supports via
/// <see cref="Capabilities"/>; the endpoint layer gates calls on those
/// capabilities so an admin asking for "commit" against a local workspace
/// gets a clean capability error instead of a leaked git-specific message.
///
/// <para>
/// Registered as keyed services in DI — one per <see cref="WorkspaceType"/>
/// — and resolved via <see cref="IWorkspaceProviderRegistry"/>.
/// </para>
///
/// <para>
/// Verbs that aren't supported by a provider should throw
/// <see cref="System.NotSupportedException"/>. The capability check at the
/// endpoint layer is the actual gate; the throw is defense-in-depth.
/// </para>
/// </summary>
public interface IWorkspaceProvider
{
    /// <summary>Which verbs this provider implements. Drives the SPA's UI affordances (header Commit/Push buttons, etc.) via <c>GET /api/workspaces/{slug}/status</c>.</summary>
    WorkspaceCapabilities Capabilities { get; }

    /// <summary>Resolve the provider's working surface (filesystem path for git/local; null for read-only or non-filesystem providers). Used by the projection layer and the file-read endpoint.</summary>
    Task<string?> ResolveRootAsync(Workspace workspace, CancellationToken ct = default);

    /// <summary>Snapshot the workspace's pending state — uncommitted changes, unpushed commits, working-root presence. Used by the SPA's header status component and broadcast on every state-mutating verb.</summary>
    Task<WorkspaceProviderStatus> GetStatusAsync(
        Workspace workspace,
        CancellationToken ct = default
    );

    /// <summary>Apply file mutations to the workspace's content surface. For git workspaces this writes to the working tree without committing — the user batches commits explicitly via <see cref="CommitAsync"/>. For local workspaces the write is the persistent action.</summary>
    Task<WriteOutcome> WriteAsync(
        Workspace workspace,
        IReadOnlyList<WorkspaceFileChange> changes,
        CancellationToken ct = default
    );

    /// <summary>Pull-from-source semantics. For git: fetch + reset. For local: verify path. For s3 (future): refresh index. Always supported when <see cref="WorkspaceCapabilities.CanSync"/> is true.</summary>
    Task<SyncOutcome> SyncAsync(Workspace workspace, bool force, CancellationToken ct = default);

    /// <summary>Batch all uncommitted changes into one commit. Capability-gated — only providers with a commit boundary (git today; future revision-tracking providers) implement this.</summary>
    Task<CommitOutcome> CommitAsync(
        Workspace workspace,
        string commitMessage,
        CancellationToken ct = default
    );

    /// <summary>Upload local commits / staged content to the remote. Capability-gated.</summary>
    Task<PushOutcome> PushAsync(Workspace workspace, CancellationToken ct = default);
}

/// <summary>
/// Per-verb capability flags for a provider. Drives both endpoint-layer
/// gating (refuse with <c>WorkspaceCapabilityNotSupported</c> instead of
/// dispatching to a NotSupportedException) and the SPA's UI affordances
/// (the header Commit button is invisible when <see cref="CanCommit"/> is
/// false; the convention editor's Save button is enabled when
/// <see cref="CanWrite"/> is true; etc.).
/// </summary>
public sealed record WorkspaceCapabilities(
    bool CanWrite,
    bool CanCommit,
    bool CanPush,
    bool CanSync
);

/// <summary>
/// Live snapshot of a workspace's pending state. Returned by
/// <see cref="IWorkspaceProvider.GetStatusAsync"/> and broadcast over
/// SignalR (<c>workspace:&lt;slug&gt;:status</c> channel) on every successful
/// state-mutating verb so the SPA reflects the change without polling.
/// </summary>
/// <param name="UncommittedFileCount">Number of files with uncommitted changes in the working tree. Always 0 for providers without a commit boundary (local).</param>
/// <param name="UnpushedCommitCount">Number of local commits ahead of the remote. Always 0 for providers without a remote (local).</param>
/// <param name="WorkingRootExists">Whether the working surface exists on disk. False on a git workspace that's never been synced; true on a local workspace whose configured path exists; etc.</param>
public sealed record WorkspaceProviderStatus(
    int UncommittedFileCount,
    int UnpushedCommitCount,
    bool WorkingRootExists
);

/// <summary>
/// One file mutation in a <see cref="IWorkspaceProvider.WriteAsync"/> batch.
/// Mirror of the wire DTO so providers can take a stable type without
/// pulling the Web project's contracts into Core.
/// </summary>
/// <param name="Path">Workspace-relative path. Path safety is enforced at the endpoint layer; providers may re-check as defense-in-depth.</param>
/// <param name="Action">One of <c>write</c> or <c>delete</c>.</param>
/// <param name="Content">UTF-8 content for <c>write</c> actions. Ignored for <c>delete</c>.</param>
public sealed record WorkspaceFileChange(string Path, string Action, string? Content);

public sealed record WriteOutcome(
    bool Ok,
    int FilesWritten,
    string? Message,
    string? Error,
    long LatencyMs,
    DateTime At
);

public sealed record CommitOutcome(
    bool Ok,
    string? CommitSha,
    int FilesCommitted,
    bool NothingToCommit,
    string? Message,
    string? Error,
    long LatencyMs,
    DateTime At
);

public sealed record PushOutcome(
    bool Ok,
    string? Sha,
    int CommitsPushed,
    bool NothingToPush,
    string? Message,
    string? Error,
    long LatencyMs,
    DateTime At
);

public sealed record SyncOutcome(
    bool Ok,
    string? Sha,
    int DirtyCount,
    int AheadCount,
    bool RequiresForce,
    string? Message,
    string? Error,
    long LatencyMs,
    DateTime At
);

/// <summary>
/// Resolves an <see cref="IWorkspaceProvider"/> for a workspace by
/// dispatching on <see cref="Workspace.Type"/>. Wraps the
/// <c>GetRequiredKeyedService</c> lookup so endpoints don't take a direct
/// <c>IServiceProvider</c> dependency.
/// </summary>
public interface IWorkspaceProviderRegistry
{
    IWorkspaceProvider Resolve(Workspace workspace);
    IWorkspaceProvider Resolve(string workspaceType);
}
