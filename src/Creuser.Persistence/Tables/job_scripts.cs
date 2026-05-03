// Lowercase class + lowercase property names match column names exactly so
// Dapper's default mapper can populate rows without snake_case <-> PascalCase
// translation. See Tables/users.cs for the convention rationale.
#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Workspace-scoped job script. The DB row is the source-of-truth; the
/// materialized file under <c>&lt;dataDir&gt;/scripts/{type}/{slug}.{ext}</c>
/// is a sync target for IDE editing + git tracking. See architecture.md
/// "Execution model → Job script storage and frontmatter".
/// </summary>
[DmTable("cr", "job_scripts")]
[DmPrimaryKeyConstraint(["id"])]
[DmUniqueConstraint(["workspace_id", "slug"])]
[DmIndex(false, ["workspace_id", "status"])]
public class job_scripts
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("slug", length: 96, isNullable: false)]
    public string slug { get; set; } = string.Empty;

    [DmColumn("name", length: 255, isNullable: false)]
    public string name { get; set; } = string.Empty;

    [DmColumn("description", length: 1024, isNullable: true)]
    public string? description { get; set; }

    /// <summary>One of <c>deterministic</c>, <c>plan-then-execute</c>, <c>agentic</c>.</summary>
    [DmColumn("pattern", length: 32, isNullable: false)]
    public string pattern { get; set; } = "deterministic";

    /// <summary>Raw YAML frontmatter as authored — round-tripped through edits.</summary>
    [DmColumn("frontmatter", isNullable: false, providerDataType: "{postgresql:text}")]
    public string frontmatter { get; set; } = string.Empty;

    /// <summary>Raw body — for single-step jobs this is the content (LLM prompt, source code, docs).</summary>
    [DmColumn("body", isNullable: false, providerDataType: "{postgresql:text}")]
    public string body { get; set; } = string.Empty;

    /// <summary>One of <c>draft</c>, <c>active</c>, <c>disabled</c>.</summary>
    [DmColumn("status", length: 16, isNullable: false)]
    public string status { get; set; } = "draft";

    [DmColumn(
        "created_at",
        isNullable: false,
        providerDataType: "{postgresql:timestamptz}",
        defaultExpression: "CURRENT_TIMESTAMP"
    )]
    public DateTime created_at { get; set; }

    [DmColumn(
        "updated_at",
        isNullable: false,
        providerDataType: "{postgresql:timestamptz}",
        defaultExpression: "CURRENT_TIMESTAMP"
    )]
    public DateTime updated_at { get; set; }

    [DmColumn("created_by", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? created_by { get; set; }
}
