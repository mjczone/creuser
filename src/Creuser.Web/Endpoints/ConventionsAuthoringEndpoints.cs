using System.Text.Json;
using System.Text.Json.Nodes;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Execution;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Projections.Accessors;
using Creuser.Projections.Authoring;
using Creuser.Projections.Conventions;
using Creuser.Projections.Scanner;
using Creuser.Projections.Schema;
using Creuser.Web.Contracts;
using Creuser.Web.Workspaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

// ---------- Request shapes ----------

/// <summary>
/// Add or update a relationship rule. <see cref="Source"/>, <see cref="Filter"/>,
/// and <see cref="TargetKind"/> accept the YAML-equivalent shape: a string
/// shorthand (<c>frontmatter.related</c>), an object form, or a list (target_kind).
/// </summary>
public sealed record RelationshipEditRequest(
    string Kind,
    string? Name,
    string? Icon,
    string? Description,
    int? Order,
    JsonElement? Source,
    JsonElement? Filter,
    string? Interpret,
    JsonElement? TargetKind,
    string? Inverse,
    string? InverseName,
    string? InverseIcon,
    Dictionary<string, string>? Metadata
);

public sealed record ValidateConventionRequest(string Yaml);

public sealed record TestConventionRequest(string AgainstPath);

// ---------- Response shapes ----------

public sealed record ConventionCapabilitiesResult(
    string SchemaUrl,
    string SchemaVersion,
    JsonObject Schema,
    IReadOnlyList<AccessorNamespaceDto> Accessors,
    IReadOnlyList<string> WorkspaceKinds,
    IReadOnlyList<string> InterpretModes,
    IReadOnlyList<RefSourceKindDto> RefSourceKinds,
    IReadOnlyList<FilterKindDto> FilterKinds,
    IReadOnlyList<CommonPatternDto> CommonPatterns
);

public sealed record AccessorNamespaceDto(
    string Namespace,
    string? Description,
    IReadOnlyList<AccessorFieldDto> Fields
);

public sealed record AccessorFieldDto(string Name, string Description, string ReturnType);

public sealed record RefSourceKindDto(string Kind, string Description, string? Example);

public sealed record FilterKindDto(string Kind, string Description, string? PatternExample);

public sealed record CommonPatternDto(string Id, string Description, string YamlSnippet);

public sealed record ValidateConventionResult(
    bool IsValid,
    ConventionSummary? Convention,
    IReadOnlyList<ConventionLoadError> Errors
);

public sealed record TestConventionResultDto(
    bool Matched,
    EntitySummary? Entity,
    IReadOnlyList<EntityRefSummary> Refs,
    string? Error
);

public sealed record RelationshipEditResultDto(
    string ConventionId,
    string ResultingYaml,
    int RelationshipCount
);

// ---------- Endpoints ----------

/// <summary>
/// Convention authoring surface — the structured-edit ops the AI assistant
/// (and CLI / SPA editor) call instead of writing YAML directly.
///
/// <para>
/// Reads (<c>capabilities</c>, <c>validate</c>, <c>test</c>) require any
/// signed-in user. Mutations (<c>add</c>/<c>update</c>/<c>remove</c>
/// relationship) require Admin — they rewrite workspace files and a sloppy
/// edit is harder to recover from than a stray query.
/// </para>
/// </summary>
public static class ConventionsAuthoringEndpoints
{
    public static IEndpointRouteBuilder MapConventionsAuthoringEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/api/workspaces/{slug}/conventions")
            .WithTags("Projections")
            .RequireAuthorization();

        // Read-only: schema + accessor + workspace-kind discovery for the assistant.
        group
            .MapGet("/capabilities", (Delegate)GetCapabilities)
            .WithName("GetConventionCapabilities");

        // Read-only: validate a YAML string without touching the filesystem.
        group.MapPost("/validate", (Delegate)Validate).WithName("ValidateConvention");

        // Read-only: dry-run a single convention against one path.
        group.MapPost("/{id}/test", (Delegate)TestConvention).WithName("TestConvention");

        // Admin-only mutations.
        var adminGroup = app.MapGroup("/api/workspaces/{slug}/conventions")
            .WithTags("Projections")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        adminGroup
            .MapPost("/{id}/relationships", (Delegate)AddRelationship)
            .WithName("AddConventionRelationship");
        adminGroup
            .MapPut("/{id}/relationships/{kind}", (Delegate)UpdateRelationship)
            .WithName("UpdateConventionRelationship");
        adminGroup
            .MapDelete("/{id}/relationships/{kind}", (Delegate)RemoveRelationship)
            .WithName("RemoveConventionRelationship");

        return app;
    }

    private static async Task<
        Results<Ok<ApiResult<ConventionCapabilitiesResult>>, ProblemHttpResult>
    > GetCapabilities(
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

        var workingPath = await tree.ResolvePathAsync(ws, ct);
        var workspaceKinds = string.IsNullOrEmpty(workingPath)
            ? Array.Empty<string>()
            : (await loader.LoadAsync(ws, workingPath, ct))
                .Conventions.Select(c => c.Id)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

        var registry = ComputedAccessorRegistry.Default;
        var schema = ConventionSchemaGenerator.Generate(registry, workspaceKinds);
        var accessors = registry
            .Namespaces.Select(ns => new AccessorNamespaceDto(
                ns.Namespace,
                ns.Description,
                ns.Fields.Values.Select(f => new AccessorFieldDto(
                        f.Name,
                        f.Description,
                        f.ReturnType.ToString()
                    ))
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList()
            ))
            .OrderBy(n => n.Namespace, StringComparer.Ordinal)
            .ToList();

        var capabilities = new ConventionCapabilitiesResult(
            SchemaUrl: "/schemas/conventions/v1.json",
            SchemaVersion: ConventionSchemaGenerator.SchemaVersion,
            Schema: schema,
            Accessors: accessors,
            WorkspaceKinds: workspaceKinds,
            InterpretModes: new[] { "auto", "path", "slug", "glob", "url", "ref-object" },
            RefSourceKinds: new[]
            {
                new RefSourceKindDto(
                    "frontmatter",
                    "Read values from a frontmatter key on the matched file.",
                    "frontmatter.related"
                ),
                new RefSourceKindDto(
                    "path-template",
                    "Interpolate {file_dir} / {parent_dir} into a relative path and resolve as one entity.",
                    "path-template:{file_dir}/index.md"
                ),
                new RefSourceKindDto(
                    "glob",
                    "A glob pattern resolved against the working tree; each match becomes one ref.",
                    "glob:packages/database/**/*.ts"
                ),
                new RefSourceKindDto(
                    "literal",
                    "Static value list declared inline in the convention itself.",
                    null
                ),
            },
            FilterKinds: new[]
            {
                new FilterKindDto(
                    "glob",
                    "Match each consumed value against a glob. Prefix with '!' to negate.",
                    "docs/ADR/**/*.md"
                ),
                new FilterKindDto("regex", "Match against a regex.", "^v\\d+/"),
                new FilterKindDto(
                    "type",
                    "Filter by post-classification type: path | glob | url | slug.",
                    "url"
                ),
            },
            CommonPatterns: new[]
            {
                new CommonPatternDto(
                    "related-symmetric",
                    "Symmetric `related` field: one rule, kind-agnostic, mirrored inverse.",
                    """
                    - kind: related
                      name: Related
                      source: frontmatter.related
                      interpret: auto
                      target_kind: any
                      inverse: related
                    """
                ),
                new CommonPatternDto(
                    "supersedes-pair",
                    "Directional supersedes/superseded_by ADR pair.",
                    """
                    - kind: supersedes
                      name: Supersedes
                      source: frontmatter.supersedes
                      interpret: auto
                      target_kind: adr
                      inverse: superseded_by
                      inverse_name: Superseded by
                    """
                ),
                new CommonPatternDto(
                    "carve-by-filter",
                    "Carve a single `related` list into per-kind folders via filters.",
                    """
                    - kind: related_adrs
                      name: Related ADRs
                      source: frontmatter.related
                      filter: { kind: glob, pattern: "docs/ADR/**/*.md" }
                      interpret: path
                      target_kind: adr
                    """
                ),
            }
        );

        return TypedResults.Ok(new ApiResult<ConventionCapabilitiesResult>(capabilities));
    }

    private static async Task<
        Results<Ok<ApiResult<ValidateConventionResult>>, ProblemHttpResult>
    > Validate(
        string slug,
        ValidateConventionRequest request,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        ConventionEditor editor,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var v = editor.Validate(request.Yaml);
        return TypedResults.Ok(
            new ApiResult<ValidateConventionResult>(
                new ValidateConventionResult(
                    IsValid: v.IsValid,
                    Convention: v.Convention is null ? null : ToSummary(v.Convention),
                    Errors: v.Errors
                )
            )
        );
    }

    private static async Task<
        Results<Ok<ApiResult<TestConventionResultDto>>, ProblemHttpResult>
    > TestConvention(
        string slug,
        string id,
        TestConventionRequest request,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IConventionLoader loader,
        ProjectionScanner scanner,
        ConventionEditor editor,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var ws = access.Workspace;

        var test = await editor.TestAsync(ws, id, request.AgainstPath, loader, scanner, ct);
        return TypedResults.Ok(
            new ApiResult<TestConventionResultDto>(
                new TestConventionResultDto(
                    Matched: test.Matched,
                    Entity: test.Entity is null
                        ? null
                        : new EntitySummary(
                            test.Entity.Id,
                            test.Entity.Kind,
                            test.Entity.Slug,
                            test.Entity.Path,
                            test.Entity.ConventionId,
                            test.Entity.ContentHash,
                            test.Entity.MetadataJson
                        ),
                    Refs: test.Refs.Select(r => new EntityRefSummary(
                            r.Id,
                            r.ToEntityId,
                            r.Relationship,
                            r.TargetKind,
                            r.TargetSlug
                        ))
                        .ToList(),
                    Error: test.Error
                )
            )
        );
    }

    private static async Task<
        Results<
            Ok<ApiResult<RelationshipEditResultDto>>,
            BadRequest<ApiResult<RelationshipEditResultDto>>,
            ProblemHttpResult
        >
    > AddRelationship(
        string slug,
        string id,
        RelationshipEditRequest request,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        ConventionEditor editor,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var result = await editor.AddRelationshipAsync(access.Workspace, id, ToEdit(request), ct);
        return ToHttp(result, id);
    }

    private static async Task<
        Results<
            Ok<ApiResult<RelationshipEditResultDto>>,
            BadRequest<ApiResult<RelationshipEditResultDto>>,
            ProblemHttpResult
        >
    > UpdateRelationship(
        string slug,
        string id,
        string kind,
        RelationshipEditRequest request,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        ConventionEditor editor,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var result = await editor.UpdateRelationshipAsync(
            access.Workspace,
            id,
            kind,
            ToEdit(request),
            ct
        );
        return ToHttp(result, id);
    }

    private static async Task<
        Results<
            Ok<ApiResult<RelationshipEditResultDto>>,
            BadRequest<ApiResult<RelationshipEditResultDto>>,
            ProblemHttpResult
        >
    > RemoveRelationship(
        string slug,
        string id,
        string kind,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        ConventionEditor editor,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);
        var result = await editor.RemoveRelationshipAsync(access.Workspace, id, kind, ct);
        return ToHttp(result, id);
    }

    // ---------- helpers ----------

    private static Results<
        Ok<ApiResult<RelationshipEditResultDto>>,
        BadRequest<ApiResult<RelationshipEditResultDto>>,
        ProblemHttpResult
    > ToHttp(EditResult result, string conventionId)
    {
        if (!result.Succeeded)
        {
            return TypedResults.BadRequest(
                new ApiResult<RelationshipEditResultDto>(
                    new RelationshipEditResultDto(
                        ConventionId: conventionId,
                        ResultingYaml: result.Error ?? "Unknown error.",
                        RelationshipCount: -1
                    )
                )
            );
        }
        return TypedResults.Ok(
            new ApiResult<RelationshipEditResultDto>(
                new RelationshipEditResultDto(
                    ConventionId: conventionId,
                    ResultingYaml: result.ResultingYaml ?? string.Empty,
                    RelationshipCount: result.Convention!.Relationships.Count
                )
            )
        );
    }

    private static RelationshipEdit ToEdit(RelationshipEditRequest req) =>
        new(
            Kind: req.Kind,
            Name: req.Name,
            Icon: req.Icon,
            Description: req.Description,
            Order: req.Order,
            Source: FromJson(req.Source),
            Filter: FromJson(req.Filter),
            Interpret: req.Interpret,
            TargetKind: FromJson(req.TargetKind),
            Inverse: req.Inverse,
            InverseName: req.InverseName,
            InverseIcon: req.InverseIcon,
            Metadata: req.Metadata
        );

    /// <summary>
    /// Translate a JSON value to the editor's loose <see cref="object"/> shape.
    /// String → string; object → dictionary; array → list of strings.
    /// Numbers/booleans get stringified — convention scalars are always YAML
    /// strings on the wire.
    /// </summary>
    private static object? FromJson(JsonElement? element)
    {
        if (element is null)
            return null;
        var e = element.Value;
        return e.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetDouble()
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => e.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList(),
            JsonValueKind.Object => ObjectFromJson(e),
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, object?> ObjectFromJson(JsonElement obj)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            map[prop.Name] = FromJson(prop.Value);
        return map;
    }

    private static ConventionSummary ToSummary(Convention c) =>
        new(
            c.Id,
            c.Description,
            c.Priority,
            c.Match.Glob,
            c.Extends,
            c.ContentHash,
            c.SourcePath,
            c.Relationships.Select(r => new ConventionRelationshipSummary(
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
                ))
                .ToList()
        );
}
