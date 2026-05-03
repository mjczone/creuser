using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Projections;
using Creuser.Scripting.ToolLoop;
using Microsoft.Extensions.AI;

namespace Creuser.Projections.ToolLoop;

/// <summary>
/// Second <see cref="IToolLoopToolRegistry"/> in DI: exposes the workspace's
/// entity projection as a query surface for agentic
/// <c>llm-tool-loop</c> steps. Every tool reads from the
/// <see cref="IEntityStore"/> + <see cref="IEntityRefStore"/>; nothing
/// mutates. Composes alongside <c>WorkspaceToolLoopRegistry</c> — the
/// runner picks the right registry per declared tool name.
///
/// <para>
/// These tools are the agent's "compressed view" of the workspace. Where
/// the workspace toolset would force a 30-tool grep walk, the projection
/// toolset answers structural questions in one SQL filter — orders of
/// magnitude cheaper in tokens and more accurate. See
/// <c>docs/wip/projections-design.md</c> "Projection toolset" for the
/// rationale.
/// </para>
/// </summary>
public sealed class ProjectionToolLoopRegistry : IToolLoopToolRegistry
{
    public static IReadOnlyList<string> ToolNames { get; } =
        new[]
        {
            "list_kinds",
            "query_entities",
            "get_entity",
            "find_references",
            "find_orphans",
            "find_unresolved_refs",
        };

    public IReadOnlyList<string> AvailableTools => ToolNames;

    private readonly IEntityStore _entities;
    private readonly IEntityRefStore _refs;

    public ProjectionToolLoopRegistry(IEntityStore entities, IEntityRefStore refs)
    {
        _entities = entities;
        _refs = refs;
    }

    public IReadOnlyList<AIFunction> BuildTools(
        IReadOnlyList<string> names,
        StepContext ctx,
        ToolLogSink sink
    )
    {
        var workspaceId = ctx.WorkspaceId;
        var built = new List<AIFunction>(names.Count);
        foreach (var name in names)
        {
            AIFunction tool = name switch
            {
                "list_kinds" => BuildListKinds(workspaceId, sink),
                "query_entities" => BuildQueryEntities(workspaceId, sink),
                "get_entity" => BuildGetEntity(workspaceId, sink),
                "find_references" => BuildFindReferences(workspaceId, sink),
                "find_orphans" => BuildFindOrphans(workspaceId, sink),
                "find_unresolved_refs" => BuildFindUnresolvedRefs(workspaceId, sink),
                _ => throw new ToolLoopException(
                    $"Unknown projection tool '{name}'. Available: {string.Join(", ", AvailableTools)}."
                ),
            };
            built.Add(tool);
        }
        return built;
    }

    private AIFunction BuildListKinds(Guid workspaceId, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = "{}";
                try
                {
                    var all = await _entities.ListByWorkspaceAsync(workspaceId, ct);
                    var grouped = all.GroupBy(e => e.Kind)
                        .Select(g => new { kind = g.Key, count = g.Count() })
                        .OrderBy(g => g.kind, StringComparer.Ordinal)
                        .ToList();
                    var result = new { ok = true, kinds = grouped };
                    return RecordResult(sink, "list_kinds", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "list_kinds", argsJson, ex, sw);
                }
            },
            name: "list_kinds",
            description: "List every entity kind currently projected for this workspace, with counts. Use this first to discover what the workspace declares."
        );

    private AIFunction BuildQueryEntities(Guid workspaceId, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (
                [Description("Single kind to filter to. Mutually exclusive with kind_in.")]
                    string? kind,
                [Description("Multiple kinds (OR). Mutually exclusive with kind.")]
                    string[]? kind_in,
                [Description("Filter by exact slug.")] string? slug,
                [Description(
                    "Glob over the entity's path (forward slashes). e.g. 'business-rules/auth/**'."
                )]
                    string? path_glob,
                [Description("Cap on results returned. Default 50, max 500.")] int? limit,
                CancellationToken ct
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(
                    new
                    {
                        kind,
                        kind_in,
                        slug,
                        path_glob,
                        limit,
                    }
                );
                try
                {
                    var query = new EntityQuery(
                        Kind: kind,
                        KindIn: kind_in,
                        Slug: slug,
                        PathGlob: path_glob,
                        Limit: limit ?? 50
                    );
                    var matches = await _entities.QueryAsync(workspaceId, query, ct);
                    var result = new
                    {
                        ok = true,
                        entities = matches.Select(e => new
                        {
                            e.Id,
                            e.Kind,
                            e.Slug,
                            e.Path,
                            metadata = TryDeserialize(e.MetadataJson),
                        }),
                        truncated = matches.Count >= (limit ?? 50),
                    };
                    return RecordResult(sink, "query_entities", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "query_entities", argsJson, ex, sw);
                }
            },
            name: "query_entities",
            description: "Filter projected entities by kind / slug / path. Returns id, kind, slug, path, and parsed metadata. Capped at limit (default 50)."
        );

    private AIFunction BuildGetEntity(Guid workspaceId, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (
                [Description("Entity kind (e.g. 'business_rule').")] string kind,
                [Description("Entity slug.")] string slug,
                CancellationToken ct
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { kind, slug });
                try
                {
                    var entity = await _entities.FindAsync(workspaceId, kind, slug, ct);
                    if (entity is null)
                    {
                        var miss = new { ok = false, error = $"No entity {kind}/{slug}." };
                        return RecordResult(sink, "get_entity", argsJson, miss, sw);
                    }
                    var refsOut = await _refs.ListByFromAsync(entity.Id, ct);
                    var refsIn = await _refs.ListByToAsync(entity.Id, ct);
                    var result = new
                    {
                        ok = true,
                        entity = new
                        {
                            entity.Id,
                            entity.Kind,
                            entity.Slug,
                            entity.Path,
                            metadata = TryDeserialize(entity.MetadataJson),
                        },
                        refs_out = refsOut.Select(r => new
                        {
                            r.Relationship,
                            r.TargetKind,
                            r.TargetSlug,
                            resolved = r.ToEntityId.HasValue,
                        }),
                        refs_in = refsIn.Select(r => new { r.Relationship, r.FromEntityId }),
                    };
                    return RecordResult(sink, "get_entity", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "get_entity", argsJson, ex, sw);
                }
            },
            name: "get_entity",
            description: "Fetch a single entity by (kind, slug). Returns the entity + its incoming and outgoing edges."
        );

    private AIFunction BuildFindReferences(Guid workspaceId, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (
                [Description("Target entity kind.")] string kind,
                [Description("Target entity slug.")] string slug,
                [Description("Optional relationship filter (e.g. 'parent', 'implements').")]
                    string? relationship,
                CancellationToken ct
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(
                    new
                    {
                        kind,
                        slug,
                        relationship,
                    }
                );
                try
                {
                    var entity = await _entities.FindAsync(workspaceId, kind, slug, ct);
                    if (entity is null)
                    {
                        var miss = new { ok = false, error = $"No entity {kind}/{slug}." };
                        return RecordResult(sink, "find_references", argsJson, miss, sw);
                    }
                    var refsIn = await _refs.ListByToAsync(entity.Id, ct);
                    if (!string.IsNullOrWhiteSpace(relationship))
                        refsIn =
                        [
                            .. refsIn.Where(r =>
                                string.Equals(
                                    r.Relationship,
                                    relationship,
                                    StringComparison.Ordinal
                                )
                            ),
                        ];
                    var result = new
                    {
                        ok = true,
                        refs = refsIn.Select(r => new { r.FromEntityId, r.Relationship }),
                    };
                    return RecordResult(sink, "find_references", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "find_references", argsJson, ex, sw);
                }
            },
            name: "find_references",
            description: "Find every entity that references the given (kind, slug). Optional relationship filter."
        );

    private AIFunction BuildFindOrphans(Guid workspaceId, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (
                [Description("Optional kind to scope the search.")] string? kind,
                CancellationToken ct
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { kind });
                try
                {
                    var orphans = await _refs.ListOrphansAsync(workspaceId, kind, ct);
                    var result = new
                    {
                        ok = true,
                        orphans = orphans.Select(o => new
                        {
                            o.FromEntityId,
                            kind = o.TargetKind,
                            slug = o.TargetSlug,
                        }),
                    };
                    return RecordResult(sink, "find_orphans", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "find_orphans", argsJson, ex, sw);
                }
            },
            name: "find_orphans",
            description: "Find entities with no incoming references. Optionally scoped to one kind. The 'lonely nodes' query."
        );

    private AIFunction BuildFindUnresolvedRefs(Guid workspaceId, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (
                [Description("Optional target kind to filter.")] string? target_kind,
                CancellationToken ct
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { target_kind });
                try
                {
                    var refs = await _refs.ListUnresolvedAsync(workspaceId, target_kind, ct);
                    var result = new
                    {
                        ok = true,
                        unresolved = refs.Select(r => new
                        {
                            r.FromEntityId,
                            r.Relationship,
                            r.TargetKind,
                            r.TargetSlug,
                        }),
                    };
                    return RecordResult(sink, "find_unresolved_refs", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "find_unresolved_refs", argsJson, ex, sw);
                }
            },
            name: "find_unresolved_refs",
            description: "Find typed edges that point at non-existent entities — the gap signal that says 'this references something that doesn't exist'."
        );

    private static object? TryDeserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    private static object RecordResult(
        ToolLogSink sink,
        string tool,
        string argsJson,
        object result,
        Stopwatch sw
    )
    {
        sw.Stop();
        sink.Record(
            new ToolLogEntry(
                Turn: sink.CurrentTurn,
                Tool: tool,
                ArgsJson: argsJson,
                ResultJson: JsonSerializer.Serialize(result),
                DurationMs: sw.ElapsedMilliseconds
            )
        );
        return result;
    }

    private static object RecordError(
        ToolLogSink sink,
        string tool,
        string argsJson,
        Exception ex,
        Stopwatch sw
    )
    {
        sw.Stop();
        var result = new { ok = false, error = ex.Message };
        sink.Record(
            new ToolLogEntry(
                Turn: sink.CurrentTurn,
                Tool: tool,
                ArgsJson: argsJson,
                ResultJson: JsonSerializer.Serialize(result),
                DurationMs: sw.ElapsedMilliseconds,
                Error: ex.Message
            )
        );
        return result;
    }
}
