namespace Creuser.Core.Execution;

/// <summary>
/// One script in <c>cr.job_scripts</c>. The DB row is the source of truth
/// (see architecture.md "Job script storage"); the materialized file under
/// <c>&lt;dataDir&gt;/scripts/{type}/{slug}.{ext}</c> is a sync target for
/// IDE editing + git tracking.
///
/// <para>
/// Scripts are workspace-scoped — workspace admins build and run them
/// against their own workspace. The same `slug` can exist in two workspaces
/// without collision. A future "platform job" mechanism (post-v1) lets
/// platform admins publish scripts visible to multiple workspaces, but it's
/// not part of v0.1.
/// </para>
/// </summary>
public sealed record JobScript(
    Guid Id,
    Guid WorkspaceId,
    /// <summary>URL-safe slug, unique per workspace. Used in routes and as the file basename.</summary>
    string Slug,
    string Name,
    string? Description,
    /// <summary>One of <see cref="JobPattern.Deterministic"/> / <see cref="JobPattern.PlanThenExecute"/> / <see cref="JobPattern.Agentic"/>.</summary>
    string Pattern,
    /// <summary>Raw YAML frontmatter as authored. Round-trips through edits to preserve comments and ordering.</summary>
    string Frontmatter,
    /// <summary>Raw body — for single-step jobs this is the content (LLM prompt, source code, docs); for multi-step jobs the steps live in frontmatter and the body is documentation.</summary>
    string Body,
    /// <summary>One of <see cref="JobScriptStatus.Draft"/> / <see cref="JobScriptStatus.Active"/> / <see cref="JobScriptStatus.Disabled"/>.</summary>
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy
);

/// <summary>
/// Persistence contract for job scripts. Single-tenant and workspace-scoped;
/// the slug uniqueness invariant is per-workspace, not global.
/// </summary>
public interface IJobScriptStore
{
    Task<JobScript?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<JobScript?> FindBySlugAsync(Guid workspaceId, string slug, CancellationToken ct = default);
    Task<bool> SlugExistsAsync(Guid workspaceId, string slug, CancellationToken ct = default);
    Task<IReadOnlyList<JobScript>> ListByWorkspaceAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct = default
    );
    Task SaveAsync(JobScript script, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
