namespace Creuser.Core.Repositories;

/// <summary>
/// Persistence seam for workspace memberships. Reads return joined
/// member-and-user rows so the SPA's members widget renders names +
/// emails in one round trip; writes accept the (workspace_id, user_id)
/// natural key.
/// </summary>
public interface IWorkspaceMemberStore
{
    Task<IReadOnlyList<WorkspaceMemberWithUser>> ListByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default
    );

    /// <summary>
    /// True when the user has an explicit membership row OR the user's
    /// global role is Admin (admin-ness implies Editor on every workspace
    /// per the architecture's auth model). The `userIsAdmin` flag is the
    /// caller's responsibility — IUserStore is the source of truth.
    /// </summary>
    Task<bool> HasAccessAsync(
        Guid workspaceId,
        Guid userId,
        bool userIsAdmin,
        CancellationToken ct = default
    );

    /// <summary>
    /// Resolve the effective role for a user on a workspace. Returns
    /// <see cref="WorkspaceRole.Editor"/> when the user is an admin,
    /// otherwise the explicit row's role, otherwise null.
    /// </summary>
    Task<string?> GetRoleAsync(
        Guid workspaceId,
        Guid userId,
        bool userIsAdmin,
        CancellationToken ct = default
    );

    /// <summary>List the workspaces a user has explicit access to. Excludes admin-implicit access.</summary>
    Task<IReadOnlyList<Guid>> ListWorkspaceIdsForUserAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<WorkspaceMember> AddOrUpdateAsync(WorkspaceMember member, CancellationToken ct = default);

    Task RemoveAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
}
