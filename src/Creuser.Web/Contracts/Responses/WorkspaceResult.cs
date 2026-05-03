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
    string? LastSyncMessage
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
    /// <summary>True when the sync was refused because the working tree was dirty and the caller did not pass <c>force=true</c>. The SPA uses this to drive a confirmation dialog before retrying.</summary>
    bool RequiresForce = false
);
