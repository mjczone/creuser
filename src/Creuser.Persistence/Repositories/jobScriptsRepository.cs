// Lowercase repository name follows the lowercase-table convention.
#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Execution;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class jobScriptsRepository : IJobScriptStore
{
    private const string SchemaTable = "cr.job_scripts";
    private readonly NpgsqlDataSource _ds;

    public jobScriptsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<JobScript?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<job_scripts>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE id = @id LIMIT 1",
                new { id },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<JobScript?> FindBySlugAsync(
        Guid workspaceId,
        string slug,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<job_scripts>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE workspace_id = @workspaceId AND slug = @slug LIMIT 1",
                new { workspaceId, slug },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> SlugExistsAsync(
        Guid workspaceId,
        string slug,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT COUNT(*) FROM {SchemaTable} WHERE workspace_id = @workspaceId AND slug = @slug",
                new { workspaceId, slug },
                cancellationToken: ct
            )
        );
        return count > 0;
    }

    public async Task<IReadOnlyList<JobScript>> ListByWorkspaceAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<job_scripts>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE workspace_id = @workspaceId ORDER BY name OFFSET @skip LIMIT @take",
                new
                {
                    workspaceId,
                    skip,
                    take,
                },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task SaveAsync(JobScript script, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable}
                  (id, workspace_id, slug, name, description, pattern, frontmatter, body, status, created_at, updated_at, created_by)
                VALUES
                  (@id, @workspace_id, @slug, @name, @description, @pattern, @frontmatter, @body, @status, @created_at, @updated_at, @created_by)
                ON CONFLICT (id) DO UPDATE SET
                  slug        = EXCLUDED.slug,
                  name        = EXCLUDED.name,
                  description = EXCLUDED.description,
                  pattern     = EXCLUDED.pattern,
                  frontmatter = EXCLUDED.frontmatter,
                  body        = EXCLUDED.body,
                  status      = EXCLUDED.status,
                  updated_at  = CURRENT_TIMESTAMP
                """,
                ToRow(script),
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

    private static JobScript ToDomain(job_scripts r) =>
        new(
            r.id,
            r.workspace_id,
            r.slug,
            r.name,
            r.description,
            r.pattern,
            r.frontmatter,
            r.body,
            r.status,
            r.created_at,
            r.updated_at,
            r.created_by
        );

    private static job_scripts ToRow(JobScript s) =>
        new()
        {
            id = s.Id,
            workspace_id = s.WorkspaceId,
            slug = s.Slug,
            name = s.Name,
            description = s.Description,
            pattern = s.Pattern,
            frontmatter = s.Frontmatter,
            body = s.Body,
            status = s.Status,
            created_at = s.CreatedAt,
            updated_at = s.UpdatedAt,
            created_by = s.CreatedBy,
        };
}
