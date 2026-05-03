#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// One persisted <c>JobPlan</c> emitted by an <c>llm-planner</c> step.
/// See architecture.md "Three execution patterns" — plan-then-execute.
/// </summary>
[DmTable("cr", "job_plans")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["workspace_id", "created_at"])]
[DmIndex(false, ["job_script_id", "created_at"])]
public class job_plans
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("job_script_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid job_script_id { get; set; }

    [DmColumn("goal", isNullable: false)]
    public string goal { get; set; } = string.Empty;

    /// <summary>Serialized list of <c>JobPlanStep</c> as JSON.</summary>
    [DmColumn("steps", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string steps { get; set; } = "[]";

    [DmColumn("reasoning", isNullable: true)]
    public string? reasoning { get; set; }

    [DmColumn("model", length: 128, isNullable: false)]
    public string model { get; set; } = string.Empty;

    [DmColumn("provider", length: 32, isNullable: false)]
    public string provider { get; set; } = string.Empty;

    [DmColumn("tokens_used", isNullable: true, providerDataType: "{postgresql:bigint}")]
    public long? tokens_used { get; set; }

    [DmColumn(
        "created_at",
        isNullable: false,
        providerDataType: "{postgresql:timestamptz}",
        defaultExpression: "CURRENT_TIMESTAMP"
    )]
    public DateTime created_at { get; set; }
}
