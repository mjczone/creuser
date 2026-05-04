#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Repositories;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

/// <summary>
/// DapperMatic-backed implementation of <see cref="IWorkspaceMemberStore"/>.
/// Joins on <c>cr.users</c> for list queries so members render with
/// display names + emails in one round trip.
/// </summary>
public sealed class workspaceMembersRepository : IWorkspaceMemberStore
{
    private const string Table = "cr.workspace_members";
    private const string UsersTable = "cr.users";
    private readonly NpgsqlDataSource _ds;

    public workspaceMembersRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<IReadOnlyList<WorkspaceMemberWithUser>> ListByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MemberRow>(
            new CommandDefinition(
                $"""
                SELECT
                  m.workspace_id  AS WorkspaceId,
                  m.user_id       AS UserId,
                  u.email         AS Email,
                  u.display_name  AS DisplayName,
                  m.role          AS Role,
                  m.granted_at    AS GrantedAt,
                  m.granted_by    AS GrantedBy,
                  u.is_active     AS IsActive
                FROM {Table} m
                JOIN {UsersTable} u ON u.id = m.user_id
                WHERE m.workspace_id = @workspaceId
                ORDER BY u.display_name ASC
                """,
                new { workspaceId },
                cancellationToken: ct
            )
        );
        return rows.Select(r => new WorkspaceMemberWithUser(
                r.WorkspaceId,
                r.UserId,
                r.Email,
                r.DisplayName,
                r.Role,
                r.GrantedAt,
                r.GrantedBy,
                r.IsActive
            ))
            .ToList();
    }

    public async Task<bool> HasAccessAsync(
        Guid workspaceId,
        Guid userId,
        bool userIsAdmin,
        CancellationToken ct = default
    )
    {
        if (userIsAdmin)
            return true;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(
                $"SELECT COUNT(1) FROM {Table} WHERE workspace_id = @workspaceId AND user_id = @userId",
                new { workspaceId, userId },
                cancellationToken: ct
            )
        );
        return count > 0;
    }

    public async Task<string?> GetRoleAsync(
        Guid workspaceId,
        Guid userId,
        bool userIsAdmin,
        CancellationToken ct = default
    )
    {
        if (userIsAdmin)
            return WorkspaceRole.Editor;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                $"SELECT role FROM {Table} WHERE workspace_id = @workspaceId AND user_id = @userId",
                new { workspaceId, userId },
                cancellationToken: ct
            )
        );
    }

    public async Task<IReadOnlyList<Guid>> ListWorkspaceIdsForUserAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(
            new CommandDefinition(
                $"SELECT workspace_id FROM {Table} WHERE user_id = @userId",
                new { userId },
                cancellationToken: ct
            )
        );
        return rows.ToList();
    }

    public async Task<WorkspaceMember> AddOrUpdateAsync(
        WorkspaceMember member,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<workspace_members>(
            new CommandDefinition(
                $"""
                INSERT INTO {Table}
                  (workspace_id, user_id, role, granted_by)
                VALUES
                  (@workspaceId, @userId, @role, @grantedBy)
                ON CONFLICT (workspace_id, user_id) DO UPDATE SET
                  role = EXCLUDED.role,
                  granted_at = CURRENT_TIMESTAMP,
                  granted_by = EXCLUDED.granted_by
                RETURNING *
                """,
                new
                {
                    workspaceId = member.WorkspaceId,
                    userId = member.UserId,
                    role = member.Role,
                    grantedBy = member.GrantedBy,
                },
                cancellationToken: ct
            )
        );
        return new WorkspaceMember(
            row.workspace_id,
            row.user_id,
            row.role,
            row.granted_at,
            row.granted_by
        );
    }

    public async Task RemoveAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"DELETE FROM {Table} WHERE workspace_id = @workspaceId AND user_id = @userId",
                new { workspaceId, userId },
                cancellationToken: ct
            )
        );
    }

    // Dapper materializes rows from PascalCase column aliases via this
    // intermediate so the public record stays a clean domain type.
    private sealed record MemberRow(
        Guid WorkspaceId,
        Guid UserId,
        string Email,
        string DisplayName,
        string Role,
        DateTime GrantedAt,
        Guid? GrantedBy,
        bool IsActive
    );
}
