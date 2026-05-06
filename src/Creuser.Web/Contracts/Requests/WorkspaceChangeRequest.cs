namespace Creuser.Web.Contracts.Requests;

/// <summary>
/// One file mutation applied as part of a <see cref="WorkspaceChangeRequest"/>.
/// Paths are workspace-relative (e.g. <c>.creuser/conventions/adr.yaml</c>);
/// absolute paths and paths containing <c>..</c> segments are rejected by
/// the endpoint. <see cref="Action"/> drives the operation:
///   <list type="bullet">
///     <item><c>write</c> — create the file (and any missing parent
///       directories) or overwrite it. <see cref="Content"/> required.</item>
///     <item><c>delete</c> — remove the file from the working tree. The
///       endpoint is a no-op if the path doesn't exist. <see cref="Content"/>
///       is ignored.</item>
///   </list>
/// Binary files are out of scope for v1 — pass UTF-8 text via
/// <see cref="Content"/>. A separate base64 path lands when an actual
/// consumer needs it.
/// </summary>
public sealed record WorkspaceFileChange(
    string Path,
    /// <summary>One of <c>write</c> or <c>delete</c>.</summary>
    string Action,
    string? Content = null
);

/// <summary>
/// Apply a batch of file mutations to a workspace's working surface.
/// Provider-dispatched: git workspaces write to the working tree without
/// committing, local workspaces write directly to disk. Commit and push
/// are <strong>separate</strong> operations exposed by their own
/// endpoints — the platform doesn't bundle write + commit + push into
/// one request because the natural cadence for those is admin-driven,
/// not save-driven.
/// </summary>
public sealed record WorkspaceChangeRequest(IReadOnlyList<WorkspaceFileChange> Changes);

/// <summary>
/// Batch all uncommitted changes in the workspace into a single git
/// commit. Capability-gated to providers that support a commit boundary
/// (git today; future revision-tracking providers). The platform's
/// header chrome surfaces this verb only when the active workspace's
/// provider declares <c>CanCommit</c>.
/// </summary>
public sealed record WorkspaceCommitRequest(string CommitMessage);
