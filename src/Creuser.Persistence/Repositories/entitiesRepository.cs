#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Projections;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class entitiesRepository : IEntityStore
{
    private const string EntitiesTable = "cr.entities";
    private const string EntityRefsTable = "cr.entity_refs";
    private readonly NpgsqlDataSource _ds;

    public entitiesRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<IReadOnlyList<EntityProjection>> ListByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<entities>(
            new CommandDefinition(
                $"SELECT * FROM {EntitiesTable} WHERE workspace_id = @workspaceId ORDER BY kind, slug",
                new { workspaceId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<EntityProjection?> FindAsync(
        Guid workspaceId,
        string kind,
        string slug,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<entities>(
            new CommandDefinition(
                $"SELECT * FROM {EntitiesTable} WHERE workspace_id = @workspaceId AND kind = @kind AND slug = @slug LIMIT 1",
                new
                {
                    workspaceId,
                    kind,
                    slug,
                },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<EntityProjection>> QueryAsync(
        Guid workspaceId,
        EntityQuery query,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var sql = $"SELECT * FROM {EntitiesTable} WHERE workspace_id = @workspaceId";
        var p = new DynamicParameters();
        p.Add("workspaceId", workspaceId);
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            sql += " AND kind = @kind";
            p.Add("kind", query.Kind);
        }
        else if (query.KindIn is { Count: > 0 })
        {
            sql += " AND kind = ANY(@kindIn)";
            p.Add("kindIn", query.KindIn.ToArray());
        }
        if (!string.IsNullOrWhiteSpace(query.Slug))
        {
            sql += " AND slug = @slug";
            p.Add("slug", query.Slug);
        }
        if (!string.IsNullOrWhiteSpace(query.PathGlob))
        {
            // Convert glob to LIKE with %; covers the common case (suffix /
            // prefix wildcards). Real glob translation lands when matrix
            // views need richer querying.
            var like = query.PathGlob.Replace('*', '%').Replace('?', '_');
            sql += " AND path LIKE @likePath";
            p.Add("likePath", like);
        }
        sql += " ORDER BY kind, slug LIMIT @limit";
        p.Add("limit", Math.Max(1, Math.Min(500, query.Limit)));

        var rows = await conn.QueryAsync<entities>(
            new CommandDefinition(sql, p, cancellationToken: ct)
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task ReplaceAllAsync(
        Guid workspaceId,
        IReadOnlyList<EntityProjection> projections,
        IReadOnlyList<EntityRefProjection> refs,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    $"DELETE FROM {EntityRefsTable} WHERE workspace_id = @workspaceId",
                    new { workspaceId },
                    transaction: tx,
                    cancellationToken: ct
                )
            );
            await conn.ExecuteAsync(
                new CommandDefinition(
                    $"DELETE FROM {EntitiesTable} WHERE workspace_id = @workspaceId",
                    new { workspaceId },
                    transaction: tx,
                    cancellationToken: ct
                )
            );

            if (projections.Count > 0)
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        $"""
                        INSERT INTO {EntitiesTable}
                          (id, workspace_id, kind, slug, path, convention_id, metadata, content_hash, last_seen_at)
                        VALUES
                          (@id, @workspace_id, @kind, @slug, @path, @convention_id, @metadata::jsonb, @content_hash, @last_seen_at)
                        """,
                        projections.Select(ToRow).ToArray(),
                        transaction: tx,
                        cancellationToken: ct
                    )
                );
            }

            if (refs.Count > 0)
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        $"""
                        INSERT INTO {EntityRefsTable}
                          (id, workspace_id, from_entity_id, to_entity_id, relationship, target_kind, target_slug, metadata)
                        VALUES
                          (@id, @workspace_id, @from_entity_id, @to_entity_id, @relationship, @target_kind, @target_slug, @metadata::jsonb)
                        """,
                        refs.Select(ToRefRow).ToArray(),
                        transaction: tx,
                        cancellationToken: ct
                    )
                );
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> CountByKindAsync(
        Guid workspaceId,
        string kind,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT COUNT(*) FROM {EntitiesTable} WHERE workspace_id = @workspaceId AND kind = @kind",
                new { workspaceId, kind },
                cancellationToken: ct
            )
        );
    }

    private static EntityProjection ToDomain(entities r) =>
        new(
            r.id,
            r.workspace_id,
            r.kind,
            r.slug,
            r.path,
            r.convention_id,
            r.metadata,
            r.content_hash,
            r.last_seen_at
        );

    private static entities ToRow(EntityProjection e) =>
        new()
        {
            id = e.Id,
            workspace_id = e.WorkspaceId,
            kind = e.Kind,
            slug = e.Slug,
            path = e.Path,
            convention_id = e.ConventionId,
            metadata = e.MetadataJson,
            content_hash = e.ContentHash,
            last_seen_at = e.LastSeenAt,
        };

    private static entity_refs ToRefRow(EntityRefProjection r) =>
        new()
        {
            id = r.Id,
            workspace_id = r.WorkspaceId,
            from_entity_id = r.FromEntityId,
            to_entity_id = r.ToEntityId,
            relationship = r.Relationship,
            target_kind = r.TargetKind,
            target_slug = r.TargetSlug,
            metadata = r.MetadataJson,
        };
}
