using Creuser.Web.Contracts.Requests;

namespace Creuser.Web.Contracts.Responses;

/// <summary>
/// Wire shape for a workspace. The <see cref="GitSettings"/> /
/// <see cref="LocalSettings"/> fields are populated based on
/// <see cref="Type"/> — exactly one of them will be non-null. Future
/// workspace types add their own typed settings field rather than dropping
/// a polymorphic payload here, keeping TypeScript types crisp.
/// </summary>
public sealed record WorkspaceResult(
    Guid WorkspaceId,
    string Slug,
    string Name,
    string? Description,
    string Type,
    GitWorkspaceSettingsDto? GitSettings,
    LocalWorkspaceSettingsDto? LocalSettings,
    /// <summary>Whether the workspace's auth secret is currently persisted on disk. Drives the "set" / "not set" chip in the UI without ever exposing the credential value. Always false for non-git types.</summary>
    bool AuthSecretPresent,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>UTC time of the most recent sync attempt — null until the first sync.</summary>
    DateTime? LastSyncAt,
    /// <summary>Resolved commit SHA after the last successful sync. Null on failure or for non-git types.</summary>
    string? LastSyncSha,
    /// <summary>One of <c>ok</c>, <c>failed</c>, or null (never synced).</summary>
    string? LastSyncStatus,
    /// <summary>Free-text message from the last sync — git stderr on failure, success summary on success.</summary>
    string? LastSyncMessage,
    /// <summary>UTC time of the most recent push attempt — null until the first push.</summary>
    DateTime? LastPushAt,
    /// <summary>HEAD SHA at the time of the last successful push. Null on failure or for non-git types.</summary>
    string? LastPushSha,
    /// <summary>One of <c>ok</c>, <c>nothing-to-push</c>, <c>failed</c>, or null (never pushed).</summary>
    string? LastPushStatus,
    /// <summary>Free-text message from the last push — git stderr on failure, success summary on success.</summary>
    string? LastPushMessage
);

public sealed record WorkspaceSyncResult(
    bool Ok,
    string Slug,
    string? Sha,
    long LatencyMs,
    DateTime SyncedAt,
    string? Message,
    string? Error,
    /// <summary>Number of dirty paths (modified, added, untracked) detected in the working tree before the sync touched it. Non-zero on a successful sync means those changes were discarded; non-zero with <see cref="Ok"/>=false and <see cref="RequiresForce"/>=true means the sync was refused pending confirmation.</summary>
    int DirtyCount = 0,
    /// <summary>Number of local commits on the working branch that aren't reachable from the reset target (typically <c>origin/workingBranch</c>). Non-zero means a successful sync would silently destroy those commits via <c>reset --hard</c>; the sync handler treats this the same way as a dirty tree and refuses with <see cref="RequiresForce"/>=true unless the caller opts in.</summary>
    int AheadCount = 0,
    /// <summary>True when the sync was refused because the working tree was dirty <em>or</em> the local branch had unpushed commits, and the caller did not pass <c>force=true</c>. The SPA uses this to drive a confirmation dialog (citing whichever counts are non-zero) before retrying.</summary>
    bool RequiresForce = false
);

/// <summary>
/// Result of a manual or scheduled push of the working branch to the
/// remote. Mirrors <see cref="WorkspaceSyncResult"/>'s shape so the SPA
/// can reuse the same status-handling patterns. <see cref="AheadCount"/>
/// is the number of local-only commits that were detected before the
/// push ran — a clean push reports the count it just sent, a
/// nothing-to-push response reports 0, a failure reports whatever count
/// existed at decision time.
/// </summary>
public sealed record WorkspacePushResult(
    bool Ok,
    string Slug,
    /// <summary>HEAD SHA at the moment of the push. Null on failure or for nothing-to-push results.</summary>
    string? Sha,
    long LatencyMs,
    DateTime PushedAt,
    string? Message,
    string? Error,
    /// <summary>Number of local-only commits detected before push. Zero means the working branch is already up-to-date with origin.</summary>
    int AheadCount = 0,
    /// <summary>True when the working branch was already in sync with origin and the push was a no-op. <see cref="Ok"/>=true in this case.</summary>
    bool NothingToPush = false
);

/// <summary>
/// Raw contents of a single file in a workspace's working tree, plus
/// the metadata an editor surface needs to drive optimistic-concurrency
/// dirty checks (<see cref="ContentHash"/>) and size-aware UI
/// affordances. Read counterpart to <see cref="WorkspaceChangeResult"/>.
/// </summary>
public sealed record WorkspaceFileContent(
    string Path,
    string Content,
    /// <summary>SHA-256 of <see cref="Content"/> at read time. Editor surfaces can compare this against a re-fetch before save to detect concurrent edits.</summary>
    string ContentHash,
    long SizeBytes
);

/// <summary>
/// Directory listing for one folder in a workspace's working surface.
/// The file-manager widget consumes this; <see cref="Folders"/> render
/// as drill-in rows, <see cref="Files"/> as clickable preview rows.
/// </summary>
public sealed record WorkspaceFolderListing(
    /// <summary>Canonicalized request path (workspace-relative, forward-slash separators, no leading slash). Empty string at the root.</summary>
    string Path,
    IReadOnlyList<WorkspaceFolderEntry> Folders,
    IReadOnlyList<WorkspaceFileEntry> Files,
    /// <summary>True when the listing was capped — the folder has more than the per-request cap and additional entries were dropped. UI shows a "narrow your path" hint.</summary>
    bool Truncated
);

public sealed record WorkspaceFolderEntry(string Name, string Path);

/// <summary>
/// One file in a directory listing. <see cref="ContentKind"/> is a
/// hint derived from the extension so the file-manager widget knows
/// which preview to render without a second round-trip.
/// </summary>
public sealed record WorkspaceFileEntry(
    string Name,
    string Path,
    long SizeBytes,
    DateTime ModifiedAt,
    /// <summary>One of <c>text</c>, <c>image</c>, <c>binary</c>, <c>unknown</c>.</summary>
    string ContentKind
);

/// <summary>
/// Result of a batch of file writes against a workspace's working
/// surface. Write-only — there is no commit step inside this verb.
/// Commit (where supported) is a separate endpoint that batches all
/// uncommitted writes into one commit at the admin's discretion.
/// </summary>
public sealed record WorkspaceChangeResult(
    bool Ok,
    string Slug,
    long LatencyMs,
    DateTime At,
    string? Message,
    string? Error,
    int FilesChanged = 0
);

/// <summary>
/// Result of a manual commit. Mirror of <see cref="WorkspacePushResult"/>'s
/// shape — capability-gated providers (git today) implement; others
/// would never reach this endpoint due to the capability check.
/// </summary>
public sealed record WorkspaceCommitResult(
    bool Ok,
    string Slug,
    string? CommitSha,
    long LatencyMs,
    DateTime CommittedAt,
    string? Message,
    string? Error,
    int FilesCommitted = 0,
    bool NothingToCommit = false
);

/// <summary>
/// Snapshot of a workspace's pending state plus the provider's
/// capability flags. Returned by <c>GET /api/workspaces/{slug}/status</c>
/// and broadcast over SignalR on every state-mutating verb so the SPA
/// surfaces fresh counts in the header without polling.
/// </summary>
public sealed record WorkspaceStatusResult(
    string Slug,
    string Type,
    WorkspaceCapabilitiesDto Capabilities,
    int UncommittedFileCount,
    int UnpushedCommitCount,
    bool WorkingRootExists
);

/// <summary>
/// Wire-shape mirror of <c>Creuser.Core.Repositories.WorkspaceCapabilities</c>.
/// Drives the SPA's UI affordances (Commit/Push button visibility, etc.).
/// </summary>
public sealed record WorkspaceCapabilitiesDto(
    bool CanWrite,
    bool CanCommit,
    bool CanPush,
    bool CanSync
);
