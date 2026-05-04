#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Repositories;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

/// <summary>
/// DapperMatic-backed implementation of <see cref="IDashboardStore"/>.
/// Owns CRUD for both <c>cr.dashboards</c> and <c>cr.dashboard_groups</c>;
/// the nav-tree query joins both in one round trip.
/// </summary>
public sealed class dashboardsRepository : IDashboardStore
{
    private const string DashTable = "cr.dashboards";
    private const string GroupTable = "cr.dashboard_groups";
    private readonly NpgsqlDataSource _ds;

    public dashboardsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    // ============================================================
    // Groups
    // ============================================================

    public async Task<IReadOnlyList<DashboardGroup>> ListGroupsAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<dashboard_groups>(
            new CommandDefinition(
                $"""
                SELECT * FROM {GroupTable}
                WHERE workspace_id = @workspaceId
                ORDER BY position ASC, created_at ASC
                """,
                new { workspaceId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToGroupDomain).ToList();
    }

    public async Task<DashboardGroup?> FindGroupBySlugAsync(
        Guid workspaceId,
        string slug,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<dashboard_groups>(
            new CommandDefinition(
                $"SELECT * FROM {GroupTable} WHERE workspace_id = @workspaceId AND slug = @slug LIMIT 1",
                new { workspaceId, slug },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToGroupDomain(row);
    }

    public async Task<DashboardGroup> CreateGroupAsync(
        DashboardGroup group,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<dashboard_groups>(
            new CommandDefinition(
                $"""
                INSERT INTO {GroupTable}
                  (workspace_id, slug, name, icon, position, is_default, created_by)
                VALUES
                  (@workspaceId, @slug, @name, @icon, @position, @isDefault, @createdBy)
                RETURNING *
                """,
                new
                {
                    workspaceId = group.WorkspaceId,
                    slug = group.Slug,
                    name = group.Name,
                    icon = group.Icon,
                    position = group.Position,
                    isDefault = group.IsDefault,
                    createdBy = group.CreatedBy,
                },
                cancellationToken: ct
            )
        );
        return ToGroupDomain(row);
    }

    public async Task UpdateGroupAsync(DashboardGroup group, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE {GroupTable} SET
                  name = @name,
                  icon = @icon,
                  position = @position,
                  is_default = @isDefault,
                  updated_at = CURRENT_TIMESTAMP
                WHERE workspace_id = @workspaceId AND slug = @slug
                """,
                new
                {
                    workspaceId = group.WorkspaceId,
                    slug = group.Slug,
                    name = group.Name,
                    icon = group.Icon,
                    position = group.Position,
                    isDefault = group.IsDefault,
                },
                cancellationToken: ct
            )
        );
    }

    public async Task DeleteGroupAsync(
        Guid workspaceId,
        string slug,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        // Orphan child dashboards back to standalone — the FK is ON DELETE
        // SET NULL so the cascade is handled by the schema, but doing it
        // explicitly here makes the intent obvious in the repo.
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    $"""
                    UPDATE {DashTable} SET group_id = NULL, updated_at = CURRENT_TIMESTAMP
                    WHERE workspace_id = @workspaceId
                      AND group_id = (SELECT id FROM {GroupTable} WHERE workspace_id = @workspaceId AND slug = @slug)
                    """,
                    new { workspaceId, slug },
                    transaction: tx,
                    cancellationToken: ct
                )
            );
            await conn.ExecuteAsync(
                new CommandDefinition(
                    $"DELETE FROM {GroupTable} WHERE workspace_id = @workspaceId AND slug = @slug",
                    new { workspaceId, slug },
                    transaction: tx,
                    cancellationToken: ct
                )
            );
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ============================================================
    // Dashboards
    // ============================================================

    public async Task<IReadOnlyList<Dashboard>> ListAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<dashboards>(
            new CommandDefinition(
                $"""
                SELECT * FROM {DashTable}
                WHERE workspace_id = @workspaceId
                ORDER BY position ASC, created_at ASC
                """,
                new { workspaceId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDashboardDomain).ToList();
    }

    public async Task<Dashboard?> FindBySlugAsync(
        Guid workspaceId,
        string slug,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<dashboards>(
            new CommandDefinition(
                $"SELECT * FROM {DashTable} WHERE workspace_id = @workspaceId AND slug = @slug LIMIT 1",
                new { workspaceId, slug },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDashboardDomain(row);
    }

    public async Task<Dashboard> CreateAsync(Dashboard dashboard, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<dashboards>(
            new CommandDefinition(
                $"""
                INSERT INTO {DashTable}
                  (workspace_id, group_id, slug, name, icon, layout, widgets, position, is_default, created_by)
                VALUES
                  (@workspaceId, @groupId, @slug, @name, @icon, @layout::jsonb, @widgets::jsonb, @position, @isDefault, @createdBy)
                RETURNING *
                """,
                new
                {
                    workspaceId = dashboard.WorkspaceId,
                    groupId = dashboard.GroupId,
                    slug = dashboard.Slug,
                    name = dashboard.Name,
                    icon = dashboard.Icon,
                    layout = dashboard.LayoutJson,
                    widgets = dashboard.WidgetsJson,
                    position = dashboard.Position,
                    isDefault = dashboard.IsDefault,
                    createdBy = dashboard.CreatedBy,
                },
                cancellationToken: ct
            )
        );
        return ToDashboardDomain(row);
    }

    public async Task UpdateAsync(Dashboard dashboard, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE {DashTable} SET
                  group_id = @groupId,
                  name = @name,
                  icon = @icon,
                  layout = @layout::jsonb,
                  widgets = @widgets::jsonb,
                  position = @position,
                  is_default = @isDefault,
                  updated_at = CURRENT_TIMESTAMP
                WHERE workspace_id = @workspaceId AND slug = @slug
                """,
                new
                {
                    workspaceId = dashboard.WorkspaceId,
                    slug = dashboard.Slug,
                    groupId = dashboard.GroupId,
                    name = dashboard.Name,
                    icon = dashboard.Icon,
                    layout = dashboard.LayoutJson,
                    widgets = dashboard.WidgetsJson,
                    position = dashboard.Position,
                    isDefault = dashboard.IsDefault,
                },
                cancellationToken: ct
            )
        );
    }

    public async Task DeleteAsync(Guid workspaceId, string slug, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"DELETE FROM {DashTable} WHERE workspace_id = @workspaceId AND slug = @slug",
                new { workspaceId, slug },
                cancellationToken: ct
            )
        );
    }

    // ============================================================
    // Nav tree
    // ============================================================

    public async Task<DashboardNavTree> GetNavTreeAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var groups = (
            await conn.QueryAsync<dashboard_groups>(
                new CommandDefinition(
                    $"""
                    SELECT * FROM {GroupTable}
                    WHERE workspace_id = @workspaceId
                    ORDER BY position ASC, created_at ASC
                    """,
                    new { workspaceId },
                    cancellationToken: ct
                )
            )
        ).ToList();

        var dashboardRows = (
            await conn.QueryAsync<dashboards>(
                new CommandDefinition(
                    $"""
                    SELECT id, workspace_id, group_id, slug, name, icon, position
                    FROM {DashTable}
                    WHERE workspace_id = @workspaceId
                    ORDER BY position ASC, created_at ASC
                    """,
                    new { workspaceId },
                    cancellationToken: ct
                )
            )
        ).ToList();

        var navItems = dashboardRows
            .Select(d => new
            {
                Item = new DashboardNavItem(d.slug, d.name, d.icon, d.position),
                GroupId = d.group_id,
            })
            .ToList();

        var groupNodes = groups
            .Select(g => new DashboardNavGroup(
                g.slug,
                g.name,
                g.icon,
                g.position,
                navItems.Where(n => n.GroupId == g.id).Select(n => n.Item).ToList()
            ))
            .ToList();

        var standalones = navItems.Where(n => n.GroupId is null).Select(n => n.Item).ToList();

        return new DashboardNavTree(groupNodes, standalones);
    }

    // ============================================================
    // Mappers
    // ============================================================

    private static DashboardGroup ToGroupDomain(dashboard_groups r) =>
        new(
            Id: r.id,
            WorkspaceId: r.workspace_id,
            Slug: r.slug,
            Name: r.name,
            Icon: r.icon,
            Position: r.position,
            IsDefault: r.is_default,
            CreatedAt: r.created_at,
            UpdatedAt: r.updated_at,
            CreatedBy: r.created_by
        );

    private static Dashboard ToDashboardDomain(dashboards r) =>
        new(
            Id: r.id,
            WorkspaceId: r.workspace_id,
            GroupId: r.group_id,
            Slug: r.slug,
            Name: r.name,
            Icon: r.icon,
            LayoutJson: r.layout,
            WidgetsJson: r.widgets,
            Position: r.position,
            IsDefault: r.is_default,
            CreatedAt: r.created_at,
            UpdatedAt: r.updated_at,
            CreatedBy: r.created_by
        );
}
