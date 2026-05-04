namespace Creuser.Core.Repositories;

/// <summary>
/// One workspace-membership grant. Pair `(WorkspaceId, UserId)` is the
/// natural key; admins do not have rows here — their access is implicit
/// per the architecture's auth model. <see cref="Role"/> is one of
/// <see cref="WorkspaceRole.Editor"/> or <see cref="WorkspaceRole.Viewer"/>.
/// </summary>
public sealed record WorkspaceMember(
    Guid WorkspaceId,
    Guid UserId,
    string Role,
    DateTime GrantedAt,
    Guid? GrantedBy
);

/// <summary>
/// Membership row joined with the user's display attributes. The members
/// list endpoint returns this shape so the SPA can render names + emails
/// without a second round-trip per row.
/// </summary>
public sealed record WorkspaceMemberWithUser(
    Guid WorkspaceId,
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    DateTime GrantedAt,
    Guid? GrantedBy,
    bool IsActive
);

public static class WorkspaceRole
{
    public const string Editor = "Editor";
    public const string Viewer = "Viewer";

    public static bool IsValid(string role) => role is Editor or Viewer;
}
