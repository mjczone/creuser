// Lowercase repository name follows the lowercase-table convention used in
// Tables/workspaces.cs. See Repositories/usersRepository.cs for the
// rationale behind the naming.
#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Repositories;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class workspacesRepository : IWorkspaceStore
{
    private const string SchemaTable = "cr.workspaces";
    private readonly NpgsqlDataSource _ds;

    public workspacesRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<Workspace?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<workspaces>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE id = @id LIMIT 1",
                new { id },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<Workspace?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<workspaces>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE slug = @slug LIMIT 1",
                new { slug },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT COUNT(*) FROM {SchemaTable} WHERE slug = @slug",
                new { slug },
                cancellationToken: ct
            )
        );
        return count > 0;
    }

    public async Task<IReadOnlyList<Workspace>> ListAsync(
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<workspaces>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} ORDER BY name OFFSET @skip LIMIT @take",
                new { skip, take },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task SaveAsync(Workspace workspace, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable}
                  (id, slug, name, description, type, settings, created_at, updated_at, created_by)
                VALUES
                  (@id, @slug, @name, @description, @type, @settings::jsonb, @created_at, @updated_at, @created_by)
                ON CONFLICT (id) DO UPDATE SET
                  slug        = EXCLUDED.slug,
                  name        = EXCLUDED.name,
                  description = EXCLUDED.description,
                  type        = EXCLUDED.type,
                  settings    = EXCLUDED.settings,
                  updated_at  = CURRENT_TIMESTAMP
                """,
                ToRow(workspace),
                cancellationToken: ct
            )
        );
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(
                $"DELETE FROM {SchemaTable} WHERE id = @id",
                new { id },
                cancellationToken: ct
            )
        );
        return rows > 0;
    }

    public async Task UpdateSyncStatusAsync(
        Guid id,
        DateTime syncedAt,
        string status,
        string? sha,
        string? message,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE {SchemaTable}
                   SET last_sync_at      = @syncedAt,
                       last_sync_status  = @status,
                       last_sync_sha     = @sha,
                       last_sync_message = @message
                 WHERE id = @id
                """,
                new
                {
                    id,
                    syncedAt,
                    status,
                    sha,
                    message,
                },
                cancellationToken: ct
            )
        );
    }

    public async Task UpdatePushStatusAsync(
        Guid id,
        DateTime pushedAt,
        string status,
        string? sha,
        string? message,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE {SchemaTable}
                   SET last_push_at      = @pushedAt,
                       last_push_status  = @status,
                       last_push_sha     = @sha,
                       last_push_message = @message
                 WHERE id = @id
                """,
                new
                {
                    id,
                    pushedAt,
                    status,
                    sha,
                    message,
                },
                cancellationToken: ct
            )
        );
    }

    private static Workspace ToDomain(workspaces r) =>
        new(
            r.id,
            r.slug,
            r.name,
            r.description,
            r.type,
            r.settings,
            r.created_at,
            r.updated_at,
            r.created_by,
            r.last_sync_at,
            r.last_sync_sha,
            r.last_sync_status,
            r.last_sync_message,
            r.last_push_at,
            r.last_push_sha,
            r.last_push_status,
            r.last_push_message
        );

    private static workspaces ToRow(Workspace w) =>
        new()
        {
            id = w.Id,
            slug = w.Slug,
            name = w.Name,
            description = w.Description,
            type = w.Type,
            settings = w.Settings,
            created_at = w.CreatedAt,
            updated_at = w.UpdatedAt,
            created_by = w.CreatedBy,
            last_sync_at = w.LastSyncAt,
            last_sync_sha = w.LastSyncSha,
            last_sync_status = w.LastSyncStatus,
            last_sync_message = w.LastSyncMessage,
            last_push_at = w.LastPushAt,
            last_push_sha = w.LastPushSha,
            last_push_status = w.LastPushStatus,
            last_push_message = w.LastPushMessage,
        };
}
