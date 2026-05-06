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
    /// <summary>Right-click actions admins can run against entities of this kind. Surfaced in the CDFS view's per-row menu. Empty when the convention declares none.</summary>
    IReadOnlyList<ConventionAction> Actions,
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

/// <summary>
/// One right-click action a convention declares for its matched entities.
/// Surfaced in the CDFS view per-row menu. The action's runs spec
/// (<see cref="Runs"/>) names which dispatch path the SPA invokes —
/// <c>file-mutate</c>, <c>agent-prompt</c>, <c>query</c>, <c>job</c>.
///
/// <para>
/// <see cref="When"/> gates visibility per row. v0.1.x supports literal
/// equality only (e.g. <c>status == "draft"</c>); a richer expression
/// language defers until a real consumer asks.
/// </para>
///
/// <para>
/// <see cref="Confirm"/> is either <c>null</c> (dispatch immediately) or
/// <c>"required"</c> (the SPA shows a confirm dialog first).
/// </para>
/// </summary>
public sealed record ConventionAction(
    string Id,
    string Label,
    string? Icon,
    string? When,
    string? Confirm,
    ConventionActionRuns Runs
);

/// <summary>
/// What an action actually does when dispatched. <see cref="Kind"/> picks the
/// dispatch path; the other fields carry kind-specific payload (left null
/// when not applicable).
///
/// <list type="bullet">
/// <item><c>file-mutate</c> — <see cref="Script"/> names a job-script slug
/// the SPA runs against the entity's file. Reuses the file-mutate change
/// pipeline.</item>
/// <item><c>agent-prompt</c> — <see cref="Prompt"/> is the templated
/// prompt the SPA injects into the chat assistant with the entity as
/// context.</item>
/// <item><c>query</c> — <see cref="Tool"/> names a projection tool
/// (e.g. <c>find_references</c>). The SPA invokes it directly with
/// <see cref="Args"/>.</item>
/// <item><c>job</c> — <see cref="JobId"/> names a workspace job to run;
/// the SPA hits <c>POST /jobs/{id}/run</c>.</item>
/// </list>
/// </summary>
public sealed record ConventionActionRuns(
    string Kind,
    string? Script,
    string? Prompt,
    string? Tool,
    IReadOnlyDictionary<string, string>? Args,
    string? JobId,
    ConventionActionOutput? Output
);

/// <summary>
/// Where an action's output lands. <see cref="Target"/> is one of
/// <c>frontmatter.&lt;key&gt;</c> | <c>body</c> | <c>comments</c>; the SPA
/// uses this to decide how to writeback (frontmatter merge, body replace,
/// or chat-only with no writeback).
/// </summary>
public sealed record ConventionActionOutput(string Target);
