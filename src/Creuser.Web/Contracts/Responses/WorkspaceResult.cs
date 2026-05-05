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
