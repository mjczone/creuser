namespace Creuser.Core.Projections;

/// <summary>
/// One parsed convention — the in-memory result of loading a YAML file from
/// <c>.creuser/conventions/*.yaml</c> and merging any <c>extends:</c>
/// references against the standard library.
///
/// <para>
/// Conventions are workspace-scoped: two workspaces can declare different
/// conventions with the same <see cref="Id"/> without collision (the
/// <c>cr.entities</c> uniqueness key is <c>(workspace_id, kind, slug)</c>).
/// The <see cref="ContentHash"/> records the source YAML's sha256 so
/// downstream caches keyed on "did the convention change?" can invalidate.
/// </para>
///
/// <para>
/// See <c>docs/wip/projections-design.md</c> for the full grammar and
/// design rationale. Plugin-contributed convention <em>types</em> (post-v1)
/// will produce additional sub-fields here, but the top-level shape stays
/// stable.
/// </para>
/// </summary>
public sealed record Convention(
    /// <summary>Unique within the workspace; becomes the <c>kind</c> column on each entity row.</summary>
    string Id,
    string? Description,
    /// <summary>Source convention this one extends (resolved before this record is constructed). Null when none.</summary>
    string? Extends,
    /// <summary>Higher wins on glob conflict. Default 0.</summary>
    int Priority,
    ConventionMatch Match,
    ConventionSlugSpec Slug,
    ConventionMetadataSpec Metadata,
    IReadOnlyList<ConventionRelationship> Relationships,
    IReadOnlyList<ConventionValidationRule> Validation,
    /// <summary>sha256 of the source YAML — propagated into <c>ProjectionReport.ConventionVersions</c> so downstream caches can invalidate.</summary>
    string ContentHash,
    /// <summary>Filesystem path the convention was loaded from (relative to the workspace root). Null for bundled standards.</summary>
    string? SourcePath
);

/// <summary>
/// File-matching rules for a convention. <see cref="Glob"/> selects, the
/// <see cref="Exclude"/> globs subtract, and <see cref="FrontmatterMustHave"/>
/// gates on frontmatter presence.
/// </summary>
public sealed record ConventionMatch(
    string Glob,
    IReadOnlyList<string> Exclude,
    IReadOnlyList<string> FrontmatterMustHave
);

/// <summary>
/// How to derive a stable per-entity slug. One of <see cref="From"/> +
/// <see cref="Transform"/>, or a <see cref="Template"/>.
/// </summary>
public sealed record ConventionSlugSpec(
    /// <summary><c>filename</c> | <c>path</c> | <c>frontmatter.&lt;key&gt;</c> | <c>template</c>.</summary>
    string From,
    /// <summary><c>kebab</c> | <c>snake</c> | <c>lower</c> | <c>as-is</c>. Default <c>as-is</c>.</summary>
    string Transform,
    /// <summary>When <see cref="From"/> is <c>template</c>, the interpolation pattern (variables: <c>filename</c>, <c>parent_dir</c>, <c>path</c>, <c>extension</c>).</summary>
    string? Template
);

/// <summary>
/// How to extract metadata for an entity. <see cref="Source"/> = <c>frontmatter</c>
/// uses the existing <c>FrontmatterDialect</c> machinery; other sources land
/// later (header parser, filename pattern).
/// </summary>
public sealed record ConventionMetadataSpec(
    /// <summary><c>frontmatter</c> | <c>none</c>. (header / filename-pattern reserved for v0.2.)</summary>
    string Source,
    /// <summary>Computed-field map. Keys are output property names; values are dotted accessors like <c>file.line_count</c> / <c>git.last_commit_sha</c>.</summary>
    IReadOnlyDictionary<string, string> Computed,
    /// <summary>Optional JSON Schema-style required-fields list. Validation surfaces in <c>find_invalid</c>.</summary>
    IReadOnlyList<string> Required
);

/// <summary>
/// One typed edge between entities. The <c>relationship</c> column on
/// <c>cr.entity_refs</c> takes <see cref="Kind"/> verbatim.
/// </summary>
public sealed record ConventionRelationship(
    string Kind,
    /// <summary>Path-template resolution: interpolate variables, look up the entity at the resulting path. Mutually exclusive with <see cref="SelectFrontmatter"/>.</summary>
    string? SelectPath,
    /// <summary>Frontmatter-key resolution: read the named key, resolve each value to an entity by <c>(target_kind, slug)</c>.</summary>
    string? SelectFrontmatter,
    /// <summary>Entity kind to resolve against. Required when both endpoints exist.</summary>
    string? TargetKind
);

/// <summary>
/// One declarative validation rule. <see cref="Expr"/> is evaluated by the
/// scanner against the resolved entity's metadata + ref shape; failures
/// surface in <c>find_invalid</c>.
/// </summary>
public sealed record ConventionValidationRule(string Rule, string Expr);
