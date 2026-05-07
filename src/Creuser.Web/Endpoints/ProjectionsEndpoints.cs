using System.Text.Json;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Execution;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Projections.Conventions;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Creuser.Web.Workspaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public sealed record ConventionSummary(
    string Id,
    string? Description,
    int Priority,
    string Glob,
    string? Extends,
    string ContentHash,
    string? SourcePath,
    /// <summary>Flat relationship rules on this convention. Empty when none declared. Lets the SPA convention editor render a structured list without re-parsing YAML.</summary>
    IReadOnlyList<ConventionRelationshipSummary> Relationships
);

/// <summary>
/// Flat wire-shape for a single relationship rule. Mirrors the YAML 1:1 with
/// the structured fields exploded so client editors don't need to interpret
/// the underlying records (<c>Source</c>, <c>Filter</c>, <c>TargetKind</c>).
/// </summary>
public sealed record ConventionRelationshipSummary(
    string Kind,
    string Name,
    string? Icon,
    string? Description,
    int Order,
    string SourceKind,
    string? SourceKey,
    IReadOnlyList<string>? SourceLiterals,
    string? FilterKind,
    string? FilterPattern,
    string Interpret,
    bool TargetKindAny,
    IReadOnlyList<string> TargetKindAllowed,
    string? Inverse,
    string? InverseName,
    string? InverseIcon,
    IReadOnlyDictionary<string, string>? Metadata
);

public sealed record ConventionsListResult(
    IReadOnlyList<ConventionSummary> Conventions,
    IReadOnlyList<ConventionLoadError> Errors
);

public sealed record EntitySummary(
    Guid Id,
    string Kind,
    string Slug,
    string Path,
    string ConventionId,
    string ContentHash,
    /// <summary>Merged metadata (frontmatter + computed) as a JSON string. Same shape as <see cref="EntityDetail.MetadataJson"/>; bundled into the list response so callers (notably the CDFS view's per-row action filter) can evaluate `when:` clauses without an N+1 detail fetch.</summary>
    string MetadataJson
);

public sealed record EntityDetail(
    Guid Id,
    string Kind,
    string Slug,
    string Path,
    string ConventionId,
    string MetadataJson,
    string ContentHash,
    DateTime LastSeenAt,
    IReadOnlyList<EntityRefSummary> RefsOut,
    IReadOnlyList<EntityRefSummary> RefsIn
);

public sealed record EntityRefSummary(
    Guid Id,
    Guid? ToEntityId,
    string Relationship,
    string? TargetKind,
    string? TargetSlug
);

/// <summary>
/// One CDFS-style folder under an entity, derived from a convention's
/// <c>relationship</c> rule. Folders for outgoing edges read display info
/// from the <em>current</em> entity's convention; folders for incoming edges
/// read display info from the <em>source</em> convention's
/// <c>inverse_name</c>/<c>inverse_icon</c>.
/// </summary>
public sealed record EntityRelationshipFolder(
    string Kind,
    string Name,
    string? Icon,
    string? Description,
    int Order,
    /// <summary><c>out</c> when the current entity is the source; <c>in</c> when it's the target.</summary>
    string Direction,
    IReadOnlyList<EntityRelationshipItem> Items
);

/// <summary>
/// One target inside a relationship folder. Resolved entity references include
/// kind/slug/path; unresolved targets carry <c>metadata.kind</c> = <c>file</c>
/// or <c>url</c> with the raw value preserved.
/// </summary>
public sealed record EntityRelationshipItem(
    Guid? EntityId,
    string? Kind,
    string? Slug,
    string? Path,
    /// <summary><c>entity</c> | <c>file</c> | <c>url</c>.</summary>
    string MetadataKind,
    string? Url,
    string? Raw,
    string? ExpandedFrom
);

public sealed record EntityRelationshipsResult(
    Guid EntityId,
    string Kind,
    string Slug,
    string Path,
    IReadOnlyList<EntityRelationshipFolder> Folders
);

public sealed record SyncProjectionResult(ProjectionReport Report);

/// <summary>
/// One entry in the bundled <c>creuser:standard/*</c> library returned by
/// <c>GET /api/conventions/standard</c>. <see cref="Reference"/> is the
/// full id used in <c>extends:</c> (e.g. <c>creuser:standard/adr</c>).
/// <see cref="Yaml"/> is the raw YAML body — the same string the loader
/// merges in. Exposing it lets admins see exactly what they're inheriting
/// before they extend it.
/// </summary>
public sealed record StandardConventionEntry(string Reference, string Yaml);

public sealed record StandardConventionsListResult(
    IReadOnlyList<StandardConventionEntry> Standards
);

/// <summary>
/// One row in the CDFS (Convention-Driven File System) view's root listing.
/// Each convention becomes a top-level "folder"; clicking it drills into
/// the entities matched by that convention via the existing
/// <c>QueryEntities</c> endpoint.
///
/// <para>
/// <see cref="EntityCount"/> is computed at request time so the UI doesn't
/// need a second round-trip per row. <see cref="Actions"/> always returns
/// an empty array in v0.1.x; convention-declared right-click actions are
/// the Stage 3 slice of the file-manager design.
/// </para>
/// </summary>
public sealed record CdfsConventionRow(
    string Id,
    string? Description,
    string MatchGlob,
    int EntityCount,
    IReadOnlyList<CdfsActionDescriptor> Actions
);

/// <summary>
/// Forward-compatible shape for convention-declared actions. Empty in v0.1.x;
/// Stage 3 of the file-manager design fills this in from
/// <c>Convention.Actions</c> (not yet a field on the Core record). Keeping
/// the field on the response shape now keeps the SPA contract stable across
/// the upgrade.
/// </summary>
public sealed record CdfsActionDescriptor(
    string Id,
    string Label,
    string? Icon,
    string? When,
    string? Confirm,
    CdfsActionRuns Runs
);

public sealed record CdfsActionRuns(
    string Kind,
    string? Script,
    string? Prompt,
    string? Tool,
    IReadOnlyDictionary<string, string>? Args,
    string? JobId
);

public sealed record CdfsConventionsListResult(IReadOnlyList<CdfsConventionRow> Conventions);

/// <summary>
/// Read + sync endpoints for the workspace projection layer. Conventions
/// list / validate live here, entity browsing lives here, and the
/// manual-sync trigger that mirrors the post-sync hook lives here.
///
/// <para>
/// Auth: Admin-only for sync trigger; reads are gated to authenticated
/// users so workspace editors can browse the projection on the dashboard.
/// Future per-workspace ACLs go through the same pattern as schedules.
/// </para>
/// </summary>
public static class ProjectionsEndpoints
{
    public static IEndpointRouteBuilder MapProjectionsEndpoints(this IEndpointRouteBuilder app)
    {
        var conventions = app.MapGroup("/api/workspaces/{slug}/conventions")
            .WithTags("Projections")
            .RequireAuthorization();
        conventions.MapGet("/", (Delegate)ListConventions).WithName("ListConventions");

        // Static, workspace-agnostic library of bundled `creuser:standard/*`
        // conventions. Auth-required (any signed-in user) so admins can
        // browse the library when authoring workspace-local conventions
        // that `extends:` one of these. No mutation surface.
        var standards = app.MapGroup("/api/conventions/standard")
            .WithTags("Projections")
            .RequireAuthorization();
        standards
            .MapGet("/", (Delegate)ListStandardConventions)
            .WithName("ListStandardConventions");

        var entities = app.MapGroup("/api/workspaces/{slug}/entities")
            .WithTags("Projections")
            .RequireAuthorization();
        entities.MapGet("/", (Delegate)QueryEntities).WithName("QueryEntities");
        entities
            .MapGet("/{kind}/{entitySlug}", (Delegate)GetEntityByKindSlug)
            .WithName("GetEntity");
        entities
            .MapGet("/{kind}/{entitySlug}/relationships", (Delegate)GetEntityRelationships)
            .WithName("GetEntityRelationships");

        var projections = app.MapGroup("/api/workspaces/{slug}/projections")
            .WithTags("Projections")
            .RequireAuthorization();
        projections
            .MapPost("/sync", (Delegate)SyncProjection)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("SyncProjection");

        var cdfs = app.MapGroup("/api/workspaces/{slug}/cdfs")
            .WithTags("Projections")
            .RequireAuthorization();
        cdfs.MapGet("/conventions", (Delegate)ListCdfsConventions).WithName("ListCdfsConventions");

        return app;
    }

    [AiCapability(
        "projections.conventions",
        "projections",
        "Workspace conventions",
        "List the conventions declared in this workspace's `.creuser/conventions/` directory. Each convention says how a directory pattern (plus optional frontmatter shape) maps to entity rows in the projection. The conventions layer is the seam that turns a workspace into a queryable knowledge graph.",
        "list conventions",
        "what conventions does this workspace declare",
        "show conventions",
        Route = "/w/:slug/settings/conventions",
        RequiresRole = Roles.User
    )]
    private static async Task<
        Results<Ok<ApiResult<ConventionsListResult>>, ProblemHttpResult>
    > ListConventions(
        string slug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IConventionLoader loader,
        IWorkspaceWorkingTree tree,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var ws = access.Workspace;

        var path = await tree.ResolvePathAsync(ws, ct) ?? string.Empty;
        var loadResult = string.IsNullOrEmpty(path)
            ? new ConventionLoadResult(
                Array.Empty<Convention>(),
                new[]
                {
                    new ConventionLoadError(
                        null,
                        "Working tree is not available; sync the workspace and retry."
                    ),
                }
            )
            : await loader.LoadAsync(ws, path, ct);

        var summaries = loadResult
            .Conventions.Select(c => new ConventionSummary(
                c.Id,
                c.Description,
                c.Priority,
                c.Match.Glob,
                c.Extends,
                c.ContentHash,
                c.SourcePath,
                c.Relationships.Select(MapRelationship).ToList()
            ))
            .ToList();
        return TypedResults.Ok(
            new ApiResult<ConventionsListResult>(
                new ConventionsListResult(summaries, loadResult.Errors)
            )
        );
    }

    [AiCapability(
        "projections.standards",
        "projections",
        "Bundled convention library",
        "List the bundled `creuser:standard/*` convention library — ADR / RFC / skill / markdown-doc / migration-sql / business-rule. Each entry exposes the raw YAML so admins can see exactly what they'll inherit when they reference it via `extends:` in a workspace-local convention.",
        "list standard conventions",
        "what standard conventions are bundled",
        "show creuser standard library",
        Route = "/w/:slug/settings/conventions",
        RequiresRole = Roles.User
    )]
    private static Task<Ok<ApiResult<StandardConventionsListResult>>> ListStandardConventions()
    {
        var entries = StandardConventions
            .Library.Select(kv => new StandardConventionEntry(kv.Key, kv.Value))
            .OrderBy(e => e.Reference, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(
            TypedResults.Ok(
                new ApiResult<StandardConventionsListResult>(
                    new StandardConventionsListResult(entries)
                )
            )
        );
    }

    [AiCapability(
        "projections.cdfs",
        "projections",
        "CDFS root rows",
        "Root listing for the Convention-Driven File System view: one row per workspace convention with its entity count and any declared right-click actions. The CDFS view answers 'what does the projection actually see?' — orphan files, mis-globbed paths, and missing metadata all surface here. Drill into a row to query entities of that kind via the existing entities endpoint.",
        "show CDFS root",
        "list CDFS conventions",
        "what does the projection see",
        Route = "/w/:slug/cdfs",
        RequiresRole = Roles.User
    )]
    private static async Task<
        Results<Ok<ApiResult<CdfsConventionsListResult>>, ProblemHttpResult>
    > ListCdfsConventions(
        string slug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IConventionLoader loader,
        IEntityStore store,
        IWorkspaceWorkingTree tree,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var ws = access.Workspace;

        var path = await tree.ResolvePathAsync(ws, ct);
        IReadOnlyList<Convention> convs = string.IsNullOrEmpty(path)
            ? Array.Empty<Convention>()
            : (await loader.LoadAsync(ws, path, ct)).Conventions;

        // One projection list, group locally — saves N count round-trips for
        // workspaces with many conventions. The projection table is bounded
        // by the workspace's matched-file count, which the existing CDFS
        // mental model assumes is "small enough to enumerate."
        var entities = await store.ListByWorkspaceAsync(ws.Id, ct);
        var counts = entities
            .GroupBy(e => e.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var rows = convs
            .Select(c => new CdfsConventionRow(
                c.Id,
                c.Description,
                c.Match.Glob,
                counts.TryGetValue(c.Id, out var n) ? n : 0,
                c.Actions.Select(a => new CdfsActionDescriptor(
                        a.Id,
                        a.Label,
                        a.Icon,
                        a.When,
                        a.Confirm,
                        new CdfsActionRuns(
                            a.Runs.Kind,
                            a.Runs.Script,
                            a.Runs.Prompt,
                            a.Runs.Tool,
                            a.Runs.Args,
                            a.Runs.JobId
                        )
                    ))
                    .ToList()
            ))
            .OrderByDescending(r => r.EntityCount)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(
            new ApiResult<CdfsConventionsListResult>(new CdfsConventionsListResult(rows))
        );
    }

    [AiCapability(
        "projections.entities",
        "projections",
        "Browse projected entities",
        "Filter projected entities by kind (e.g. `business_rule`, `adr`), slug, or path glob. Each entity is a file matched by one of the workspace's conventions, with frontmatter merged into a JSON metadata blob.",
        "list entities",
        "browse entities",
        "find entities of kind",
        "what business rules exist",
        Route = "/w/:slug/entities",
        RequiresRole = Roles.User
    )]
    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<EntitySummary>>>, ProblemHttpResult>
    > QueryEntities(
        string slug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IEntityStore store,
        HttpContext http,
        string? kind,
        string? entitySlug,
        string? pathGlob,
        int? limit,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);

        var query = new EntityQuery(
            Kind: kind,
            Slug: entitySlug,
            PathGlob: pathGlob,
            Limit: limit ?? 100
        );
        var entities = await store.QueryAsync(access.Workspace.Id, query, ct);
        var summaries = entities
            .Select(e => new EntitySummary(
                e.Id,
                e.Kind,
                e.Slug,
                e.Path,
                e.ConventionId,
                e.ContentHash,
                e.MetadataJson
            ))
            .ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<EntitySummary>>(summaries));
    }

    private static async Task<
        Results<Ok<ApiResult<EntityDetail>>, ProblemHttpResult>
    > GetEntityByKindSlug(
        string slug,
        string kind,
        string entitySlug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IEntityStore store,
        IEntityRefStore refs,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);

        var entity = await store.FindAsync(access.Workspace.Id, kind, entitySlug, ct);
        if (entity is null)
            return Problems.NotFound($"No entity {kind}/{entitySlug} in workspace {slug}.");

        var refsOut = await refs.ListByFromAsync(entity.Id, ct);
        var refsIn = await refs.ListByToAsync(entity.Id, ct);

        var detail = new EntityDetail(
            entity.Id,
            entity.Kind,
            entity.Slug,
            entity.Path,
            entity.ConventionId,
            entity.MetadataJson,
            entity.ContentHash,
            entity.LastSeenAt,
            refsOut
                .Select(r => new EntityRefSummary(
                    r.Id,
                    r.ToEntityId,
                    r.Relationship,
                    r.TargetKind,
                    r.TargetSlug
                ))
                .ToList(),
            refsIn
                .Select(r => new EntityRefSummary(
                    r.Id,
                    r.ToEntityId,
                    r.Relationship,
                    r.TargetKind,
                    r.TargetSlug
                ))
                .ToList()
        );
        return TypedResults.Ok(new ApiResult<EntityDetail>(detail));
    }

    private static async Task<
        Results<Ok<ApiResult<EntityRelationshipsResult>>, ProblemHttpResult>
    > GetEntityRelationships(
        string slug,
        string kind,
        string entitySlug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IEntityStore store,
        IEntityRefStore refs,
        IConventionLoader loader,
        IWorkspaceWorkingTree tree,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var ws = access.Workspace;

        var entity = await store.FindAsync(ws.Id, kind, entitySlug, ct);
        if (entity is null)
            return Problems.NotFound($"No entity {kind}/{entitySlug} in workspace {slug}.");

        var path = await tree.ResolvePathAsync(ws, ct);
        var conventions = string.IsNullOrEmpty(path)
            ? Array.Empty<Convention>()
            : (await loader.LoadAsync(ws, path, ct)).Conventions.ToArray();

        // Index conventions by id, plus a forward (kind→rule) and inverse
        // (inverse_kind→rule) map per convention for folder display lookup.
        var conventionsById = conventions.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        var rulesByConventionAndKind =
            new Dictionary<(string conv, string kind), ConventionRelationship>();
        var rulesByConventionAndInverse =
            new Dictionary<(string conv, string kind), ConventionRelationship>();
        foreach (var c in conventions)
        {
            foreach (var r in c.Relationships)
            {
                rulesByConventionAndKind[(c.Id, r.Kind)] = r;
                if (!string.IsNullOrWhiteSpace(r.Inverse))
                    rulesByConventionAndInverse[(c.Id, r.Inverse!)] = r;
            }
        }

        // For incoming refs we need to look up the source entity to find its
        // convention. One workspace-wide list keeps this O(1) per ref.
        var allEntities = await store.ListByWorkspaceAsync(ws.Id, ct);
        var entitiesById = allEntities.ToDictionary(e => e.Id);

        var outRefs = await refs.ListByFromAsync(entity.Id, ct);
        var inRefs = await refs.ListByToAsync(entity.Id, ct);

        var folders = new List<EntityRelationshipFolder>();

        // Outgoing folders: group by relationship label, look up rule on the
        // current entity's convention.
        foreach (var group in outRefs.GroupBy(r => r.Relationship, StringComparer.Ordinal))
        {
            ConventionRelationship? rule = null;
            rulesByConventionAndKind.TryGetValue((entity.ConventionId, group.Key), out rule);
            folders.Add(BuildFolder(group, rule, "out", entitiesById));
        }

        // Incoming folders: group by relationship label. For each ref, the
        // source convention's rule with `inverse: <this label>` carries the
        // display info (inverse_name / inverse_icon).
        foreach (var group in inRefs.GroupBy(r => r.Relationship, StringComparer.Ordinal))
        {
            ConventionRelationship? rule = null;
            // Take any ref in the group; its FROM entity tells us which convention to consult.
            var sample = group.First();
            if (entitiesById.TryGetValue(sample.FromEntityId, out var fromEntity))
            {
                rulesByConventionAndInverse.TryGetValue(
                    (fromEntity.ConventionId, group.Key),
                    out rule
                );
            }
            folders.Add(BuildFolder(group, rule, "in", entitiesById, useInverseDisplay: true));
        }

        // Stable order: folder.order asc, then folder.kind asc.
        folders.Sort(
            (a, b) =>
            {
                var byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : string.CompareOrdinal(a.Kind, b.Kind);
            }
        );

        var result = new EntityRelationshipsResult(
            EntityId: entity.Id,
            Kind: entity.Kind,
            Slug: entity.Slug,
            Path: entity.Path,
            Folders: folders
        );
        return TypedResults.Ok(new ApiResult<EntityRelationshipsResult>(result));
    }

    private static EntityRelationshipFolder BuildFolder(
        IEnumerable<EntityRefProjection> refs,
        ConventionRelationship? rule,
        string direction,
        IReadOnlyDictionary<Guid, EntityProjection> entitiesById,
        bool useInverseDisplay = false
    )
    {
        var refList = refs.ToList();
        var kind = refList[0].Relationship;
        string name;
        string? icon;
        string? description;
        int order;
        if (rule is null)
        {
            name = HumanizeKind(kind);
            icon = null;
            description = null;
            order = 100;
        }
        else if (useInverseDisplay)
        {
            name = rule.InverseName ?? HumanizeKind(kind);
            icon = rule.InverseIcon;
            description = rule.Description;
            order = rule.Order;
        }
        else
        {
            name = rule.Name;
            icon = rule.Icon;
            description = rule.Description;
            order = rule.Order;
        }

        var items = refList
            .Select(r => BuildItem(r, direction, entitiesById))
            .OrderBy(i => i.Kind ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Slug ?? i.Path ?? i.Url ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        return new EntityRelationshipFolder(kind, name, icon, description, order, direction, items);
    }

    private static EntityRelationshipItem BuildItem(
        EntityRefProjection r,
        string direction,
        IReadOnlyDictionary<Guid, EntityProjection> entitiesById
    )
    {
        // For incoming refs, the "target" from the current entity's perspective
        // is the source: r.from_entity_id. For outgoing, it's r.to_entity_id.
        var targetId = direction == "in" ? r.FromEntityId : r.ToEntityId;
        EntityProjection? target = null;
        if (targetId is not null && entitiesById.TryGetValue(targetId.Value, out var t))
            target = t;

        // Parse structured ref-shape from the metadata JSONB (kind, url, expanded_from, raw).
        string metadataKind = "entity";
        string? url = null;
        string? raw = null;
        string? expandedFrom = null;
        if (!string.IsNullOrWhiteSpace(r.MetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(r.MetadataJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                    metadataKind = k.GetString()!;
                if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    url = u.GetString();
                if (root.TryGetProperty("raw", out var rw) && rw.ValueKind == JsonValueKind.String)
                    raw = rw.GetString();
                if (
                    root.TryGetProperty("expanded_from", out var ef)
                    && ef.ValueKind == JsonValueKind.String
                )
                    expandedFrom = ef.GetString();
            }
            catch
            {
                // Treat malformed metadata as bare entity ref; the resolver populates
                // valid JSON in v1+ so this branch only fires for legacy unmigrated rows.
            }
        }

        return new EntityRelationshipItem(
            EntityId: target?.Id,
            Kind: target?.Kind ?? r.TargetKind,
            Slug: target?.Slug ?? r.TargetSlug,
            Path: target?.Path,
            MetadataKind: target is not null ? "entity" : metadataKind,
            Url: url,
            Raw: raw,
            ExpandedFrom: expandedFrom
        );
    }

    private static ConventionRelationshipSummary MapRelationship(ConventionRelationship r) =>
        new(
            Kind: r.Kind,
            Name: r.Name,
            Icon: r.Icon,
            Description: r.Description,
            Order: r.Order,
            SourceKind: r.Source.Kind,
            SourceKey: r.Source.Key,
            SourceLiterals: r.Source.Literals,
            FilterKind: r.Filter?.Kind,
            FilterPattern: r.Filter?.Pattern,
            Interpret: r.Interpret.ToString().ToLowerInvariant(),
            TargetKindAny: r.TargetKind.Any,
            TargetKindAllowed: r.TargetKind.Allowed,
            Inverse: r.Inverse,
            InverseName: r.InverseName,
            InverseIcon: r.InverseIcon,
            Metadata: r.Metadata
        );

    private static string HumanizeKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return string.Empty;
        var parts = kind.Split(
            new[] { '_', '-' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return string.Join(
            ' ',
            parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..])
        );
    }

    [AiCapability(
        "projections.sync",
        "projections",
        "Run projection sync now",
        "Re-scan the working tree, apply the workspace's conventions, and rebuild the entity projection in a single transaction. Mirrors the automatic post-sync hook — useful for forcing a re-projection without re-pulling git.",
        "sync projection",
        "rebuild projection",
        "rescan entities",
        "refresh entity projection",
        Route = "/w/:slug/settings/conventions",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<
        Results<Ok<ApiResult<SyncProjectionResult>>, ProblemHttpResult>
    > SyncProjection(
        string slug,
        IWorkspaceStore workspaces,
        IProjectionSyncService service,
        IWorkspaceWorkingTree tree,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug, ct);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);

        var path = await tree.ResolvePathAsync(ws, ct) ?? string.Empty;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return Problems.NotFound(
                $"Working tree for workspace {slug} is not available. Sync the workspace first."
            );

        var report = await service.RunAsync(ws, path, ct);
        return TypedResults.Ok(
            new ApiResult<SyncProjectionResult>(new SyncProjectionResult(report))
        );
    }
}
