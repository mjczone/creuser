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
///
/// <para>
/// The display fields (<see cref="Name"/>, <see cref="Icon"/>, <see cref="Description"/>,
/// <see cref="Order"/>) are consumed by the CDFS file manager: each rule renders
/// as one navigable folder under every matching entity. The
/// <see cref="Inverse"/> + <see cref="InverseName"/> pair lets a rule declare its
/// reverse edge (folder shown on the *target* side) — symmetric for `related`,
/// directional for pairs like `supersedes` / `superseded_by`.
/// </para>
///
/// <para>
/// Resolution is expressed as <see cref="Source"/> (where values come from)
/// + <see cref="Filter"/> (which items this rule consumes) + <see cref="Interpret"/>
/// (how each consumed value becomes a target) + <see cref="TargetKind"/>
/// (kind whitelist or <c>any</c>). The legacy
/// <c>select_path</c> / <c>select_frontmatter</c> YAML keys still parse —
/// the loader translates them into the equivalent <see cref="Source"/>
/// + <see cref="Interpret"/> pair.
/// </para>
/// </summary>
public sealed record ConventionRelationship(
    /// <summary>Machine-readable edge label; written verbatim to <c>entity_refs.relationship</c>. Snake_case by convention.</summary>
    string Kind,
    /// <summary>CDFS folder name. Required at the schema level; the loader synthesizes from <see cref="Kind"/> when YAML omits it.</summary>
    string Name,
    /// <summary>Optional icon key for the CDFS folder. Matches the workspace icon registry.</summary>
    string? Icon,
    /// <summary>Optional tooltip / docs string for the rule.</summary>
    string? Description,
    /// <summary>Sort order in the per-entity CDFS folder list. Lower comes first. Default 100.</summary>
    int Order,
    /// <summary>Where the rule reads its values from.</summary>
    ConventionRefSource Source,
    /// <summary>Optional value-level filter; only items matching are consumed by this rule. Multiple rules over the same source carve a flat list into typed CDFS folders.</summary>
    ConventionRefFilter? Filter,
    /// <summary>How each consumed value is interpreted into a target.</summary>
    ConventionRefInterpret Interpret,
    /// <summary>Kind whitelist (or <c>any</c>) for slug / path lookups.</summary>
    ConventionRefTargetKind TargetKind,
    /// <summary>Per-edge metadata template; merged into <c>entity_refs.metadata</c> jsonb alongside the structured ref-shape envelope.</summary>
    IReadOnlyDictionary<string, string>? Metadata,
    /// <summary>Optional reverse edge label. Auto-creates a mirrored edge during the second pass so graph queries work both ways without duplicating frontmatter. Symmetric edges set <see cref="Inverse"/> = <see cref="Kind"/>.</summary>
    string? Inverse,
    /// <summary>CDFS folder name for the reverse edge. Defaults to <see cref="Name"/> when <see cref="Inverse"/> matches <see cref="Kind"/> (symmetric). Required (after defaulting) whenever <see cref="Inverse"/> is set.</summary>
    string? InverseName,
    /// <summary>Optional icon for the reverse-edge folder.</summary>
    string? InverseIcon
);

/// <summary>
/// Where a relationship rule reads values. <see cref="Kind"/> picks the
/// extractor; <see cref="Key"/> carries its argument (frontmatter field name,
/// path template, glob expression). For <c>literal</c>, <see cref="Literals"/>
/// holds the static list and <see cref="Key"/> is unused.
/// </summary>
public sealed record ConventionRefSource(
    /// <summary><c>frontmatter</c> | <c>path-template</c> | <c>glob</c> | <c>body-links</c> | <c>body-code-refs</c> | <c>literal</c>.</summary>
    string Kind,
    string? Key,
    IReadOnlyList<string>? Literals
);

/// <summary>
/// Optional per-rule filter applied to each yielded source value. Only items
/// matching are consumed by this rule. Lets one frontmatter field dispatch
/// into many CDFS folders.
/// </summary>
public sealed record ConventionRefFilter(
    /// <summary><c>glob</c> | <c>regex</c> | <c>type</c>. <c>type</c> filters by the post-classification ref kind: <c>path</c>, <c>glob</c>, <c>url</c>, <c>slug</c>.</summary>
    string Kind,
    string Pattern
);

/// <summary>
/// How a yielded source value becomes a target.
///
/// <list type="bullet">
/// <item><c>auto</c> — sniff the value (URL → glob → path → slug).</item>
/// <item><c>path</c> — relative path; look up entity by path, fall back to a file ref.</item>
/// <item><c>slug</c> — bare slug; look up entity by <c>(target_kind, slug)</c>.</item>
/// <item><c>glob</c> — glob expression; expand against the working tree.</item>
/// <item><c>url</c> — external URL; preserved unresolved.</item>
/// <item><c>ref-object</c> — value is a structured object like <c>{path, kind, role}</c>. Stage F.</item>
/// </list>
/// </summary>
public enum ConventionRefInterpret
{
    Auto,
    Path,
    Slug,
    Glob,
    Url,
    RefObject,
}

/// <summary>
/// Target-kind whitelist for slug/path resolution. <see cref="Any"/> means
/// kind-agnostic (resolve against all entities; ambiguity surfaces in the
/// scan report). Otherwise <see cref="Allowed"/> lists the permitted kinds.
/// </summary>
public sealed record ConventionRefTargetKind(bool Any, IReadOnlyList<string> Allowed)
{
    public static readonly ConventionRefTargetKind AnyKind = new(true, Array.Empty<string>());
}

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
