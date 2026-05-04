namespace Creuser.Core.Repositories;

/// <summary>
/// Persistence seam for dashboards + dashboard groups. The host owns
/// dashboard CRUD; the SPA hits these endpoints to list/load/save the
/// composer's state. <see cref="GetNavTreeAsync"/> is the icon-bar's
/// one-shot read used by the workspace shell.
/// </summary>
public interface IDashboardStore
{
    // Groups
    Task<IReadOnlyList<DashboardGroup>> ListGroupsAsync(
        Guid workspaceId,
        CancellationToken ct = default
    );

    Task<DashboardGroup?> FindGroupBySlugAsync(
        Guid workspaceId,
        string slug,
        CancellationToken ct = default
    );

    Task<DashboardGroup> CreateGroupAsync(DashboardGroup group, CancellationToken ct = default);

    Task UpdateGroupAsync(DashboardGroup group, CancellationToken ct = default);

    /// <summary>
    /// Delete the group and orphan its dashboards (they become standalone
    /// via <c>group_id = NULL</c>).
    /// </summary>
    Task DeleteGroupAsync(Guid workspaceId, string slug, CancellationToken ct = default);

    // Dashboards
    Task<IReadOnlyList<Dashboard>> ListAsync(Guid workspaceId, CancellationToken ct = default);

    Task<Dashboard?> FindBySlugAsync(Guid workspaceId, string slug, CancellationToken ct = default);

    Task<Dashboard> CreateAsync(Dashboard dashboard, CancellationToken ct = default);

    Task UpdateAsync(Dashboard dashboard, CancellationToken ct = default);

    /// <summary>
    /// Hard-delete the dashboard. Caller is responsible for refusing to
    /// delete <c>is_default</c> rows; the store enforces no policy itself.
    /// </summary>
    Task DeleteAsync(Guid workspaceId, string slug, CancellationToken ct = default);

    /// <summary>
    /// Pre-flattened groups + standalones for the workspace icon bar.
    /// Single round trip; sorted by position.
    /// </summary>
    Task<DashboardNavTree> GetNavTreeAsync(Guid workspaceId, CancellationToken ct = default);
}
