#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Projections;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class entityRefsRepository : IEntityRefStore
{
    private const string SchemaTable = "cr.entity_refs";
    private const string EntitiesTable = "cr.entities";
    private readonly NpgsqlDataSource _ds;

    public entityRefsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<IReadOnlyList<EntityRefProjection>> ListByFromAsync(
        Guid fromEntityId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<entity_refs>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE from_entity_id = @fromEntityId",
                new { fromEntityId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<EntityRefProjection>> ListByToAsync(
        Guid toEntityId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<entity_refs>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE to_entity_id = @toEntityId",
                new { toEntityId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<EntityRefProjection>> ListUnresolvedAsync(
        Guid workspaceId,
        string? targetKind = null,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var sql =
            $"SELECT * FROM {SchemaTable} WHERE workspace_id = @workspaceId AND to_entity_id IS NULL";
        var p = new DynamicParameters();
        p.Add("workspaceId", workspaceId);
        if (!string.IsNullOrWhiteSpace(targetKind))
        {
            sql += " AND target_kind = @targetKind";
            p.Add("targetKind", targetKind);
        }
        sql += " ORDER BY target_kind, target_slug";
        var rows = await conn.QueryAsync<entity_refs>(
            new CommandDefinition(sql, p, cancellationToken: ct)
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<EntityRefProjection>> ListOrphansAsync(
        Guid workspaceId,
        string? kind = null,
        CancellationToken ct = default
    )
    {
        // Orphans = entities with no incoming edges. Returned as
        // EntityRefProjection rows where the "from" identifies the orphan;
        // the consumer reads from_entity_id and looks up the entity. This
        // keeps the storage interface narrow — graph queries land in the
        // projection toolset itself.
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var sql = $"""
            SELECT
              gen_random_uuid()    AS id,
              e.workspace_id,
              e.id                 AS from_entity_id,
              NULL::uuid           AS to_entity_id,
              'orphan'             AS relationship,
              e.kind               AS target_kind,
              e.slug               AS target_slug,
              NULL::jsonb          AS metadata
            FROM {EntitiesTable} e
            LEFT JOIN {SchemaTable} r ON r.to_entity_id = e.id
            WHERE e.workspace_id = @workspaceId
              AND r.id IS NULL
            """;
        var p = new DynamicParameters();
        p.Add("workspaceId", workspaceId);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sql += " AND e.kind = @kind";
            p.Add("kind", kind);
        }
        sql += " ORDER BY e.kind, e.slug";
        var rows = await conn.QueryAsync<entity_refs>(
            new CommandDefinition(sql, p, cancellationToken: ct)
        );
        return rows.Select(ToDomain).ToList();
    }

    private static EntityRefProjection ToDomain(entity_refs r) =>
        new(
            r.id,
            r.workspace_id,
            r.from_entity_id,
            r.to_entity_id,
            r.relationship,
            r.target_kind,
            r.target_slug,
            r.metadata
        );
}
