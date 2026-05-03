namespace Creuser.Core.Projections;

/// <summary>
/// One projected entity: a row in <c>cr.entities</c>. Produced by the
/// <c>ProjectionScanner</c> when a file matched a convention's glob; consumed
/// by the projection toolset (<c>query_entities</c>, <c>get_entity</c>) and
/// by visualization layers built on top.
///
/// <para>
/// The <c>(WorkspaceId, Kind, Slug)</c> triple is the natural key — unique
/// per workspace. <see cref="ContentHash"/> tracks the file's sha256 at
/// scan time so downstream cache keys can invalidate when content drifts
/// without renaming or moving.
/// </para>
/// </summary>
public sealed record EntityProjection(
    Guid Id,
    Guid WorkspaceId,
    string Kind,
    string Slug,
    /// <summary>Path relative to the workspace root, forward slashes.</summary>
    string Path,
    /// <summary>Which convention produced this row. Useful for the toolset to filter by source.</summary>
    string ConventionId,
    /// <summary>Merged metadata (frontmatter + computed). JSONB on disk.</summary>
    string MetadataJson,
    /// <summary>sha256 of the file contents at scan time.</summary>
    string ContentHash,
    DateTime LastSeenAt
);

/// <summary>
/// One typed edge between two entities. <see cref="ToEntityId"/> is null
/// when the ref couldn't be resolved (target entity doesn't exist) — that's
/// the gap-finding signal surfaced by <c>find_unresolved_refs</c>.
/// </summary>
public sealed record EntityRefProjection(
    Guid Id,
    Guid WorkspaceId,
    Guid FromEntityId,
    Guid? ToEntityId,
    string Relationship,
    string? TargetKind,
    string? TargetSlug,
    /// <summary>Optional JSONB metadata on the edge — line numbers, ref-source, etc.</summary>
    string? MetadataJson
);

/// <summary>
/// Outcome of one full projection sync. Persisted alongside the run as the
/// step's primary output; surfaced via <c>GET /api/workspaces/{slug}/projections/report</c>
/// for the SPA's gap dashboard. <see cref="ConventionVersions"/> propagates
/// the per-convention content hashes so downstream LLM caches can key on
/// "did the convention change?" without re-loading YAML.
/// </summary>
public sealed record ProjectionReport(
    DateTime SyncedAt,
    long ScanDurationMs,
    int ConventionCount,
    IReadOnlyDictionary<string, int> EntitiesByKind,
    int EntityTotal,
    int RefsResolved,
    int RefsUnresolved,
    int SchemaFailures,
    int ConventionConflicts,
    IReadOnlyDictionary<string, string> ConventionVersions
);

/// <summary>
/// Storage for projected entities. Full-rebuild semantics: a sync replaces
/// everything for a workspace inside a single transaction. Soft-delete is
/// not used — the working tree is the source of truth and re-projection is
/// always cheap relative to "did this entity exist at run T?" queries
/// (which are answered by git history, not the projection).
/// </summary>
public interface IEntityStore
{
    Task<IReadOnlyList<EntityProjection>> ListByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default
    );

    Task<EntityProjection?> FindAsync(
        Guid workspaceId,
        string kind,
        string slug,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<EntityProjection>> QueryAsync(
        Guid workspaceId,
        EntityQuery query,
        CancellationToken ct = default
    );

    /// <summary>
    /// Replace all entities for a workspace in a single transaction. Used by
    /// the projection sync — operators only ever see a consistent snapshot.
    /// </summary>
    Task ReplaceAllAsync(
        Guid workspaceId,
        IReadOnlyList<EntityProjection> entities,
        IReadOnlyList<EntityRefProjection> refs,
        CancellationToken ct = default
    );

    Task<int> CountByKindAsync(Guid workspaceId, string kind, CancellationToken ct = default);
}

/// <summary>
/// Filter shape for <see cref="IEntityStore.QueryAsync"/>. Mirrors the
/// projection toolset's <c>query_entities</c> arguments — the tool calls
/// straight into this without translation.
/// </summary>
public sealed record EntityQuery(
    /// <summary>One specific kind, or null for all.</summary>
    string? Kind = null,
    /// <summary>Multiple kinds (OR). Mutually exclusive with <see cref="Kind"/>.</summary>
    IReadOnlyList<string>? KindIn = null,
    string? Slug = null,
    /// <summary>Glob over the <c>path</c> column. e.g. <c>business-rules/auth/**</c>.</summary>
    string? PathGlob = null,
    int Limit = 50
);

/// <summary>
/// Storage for typed edges between entities. Always replaced in lock-step
/// with the entity table (one transaction).
/// </summary>
public interface IEntityRefStore
{
    Task<IReadOnlyList<EntityRefProjection>> ListByFromAsync(
        Guid fromEntityId,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<EntityRefProjection>> ListByToAsync(
        Guid toEntityId,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<EntityRefProjection>> ListUnresolvedAsync(
        Guid workspaceId,
        string? targetKind = null,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<EntityRefProjection>> ListOrphansAsync(
        Guid workspaceId,
        string? kind = null,
        CancellationToken ct = default
    );
}
