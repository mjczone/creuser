#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// One execution of a <see cref="job_scripts"/>. Audit + replay live here.
/// See architecture.md "Execution model → Auditability and replay".
/// </summary>
[DmTable("cr", "job_runs")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["workspace_id", "started_at"])]
[DmIndex(false, ["job_script_id", "started_at"])]
[DmIndex(false, ["status"])]
public class job_runs
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("job_script_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid job_script_id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    /// <summary>One of <c>pending</c>, <c>running</c>, <c>paused</c>, <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>.</summary>
    [DmColumn("status", length: 16, isNullable: false)]
    public string status { get; set; } = "pending";

    /// <summary>Parameters supplied at trigger time, merged with script defaults. Stored as JSON for round-tripping.</summary>
    [DmColumn("parameters", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string parameters { get; set; } = "{}";

    [DmColumn("start_commit_sha", length: 64, isNullable: true)]
    public string? start_commit_sha { get; set; }

    [DmColumn("end_commit_sha", length: 64, isNullable: true)]
    public string? end_commit_sha { get; set; }

    [DmColumn("started_at", isNullable: false, providerDataType: "{postgresql:timestamptz}")]
    public DateTime started_at { get; set; }

    [DmColumn("completed_at", isNullable: true, providerDataType: "{postgresql:timestamptz}")]
    public DateTime? completed_at { get; set; }

    [DmColumn("triggered_by", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? triggered_by { get; set; }

    /// <summary>One of <c>manual</c>, <c>cron</c>, <c>sync</c>, <c>api</c>.</summary>
    [DmColumn("trigger_kind", length: 16, isNullable: false)]
    public string trigger_kind { get; set; } = "manual";

    [DmColumn("predecessor_run_id", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? predecessor_run_id { get; set; }

    [DmColumn("plan_id", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? plan_id { get; set; }

    [DmColumn("failure_message", length: 4096, isNullable: true)]
    public string? failure_message { get; set; }

    [DmColumn("total_tokens_used", isNullable: true, providerDataType: "{postgresql:bigint}")]
    public long? total_tokens_used { get; set; }

    [DmColumn("total_cost_usd", isNullable: true, providerDataType: "{postgresql:numeric(10, 4)}")]
    public decimal? total_cost_usd { get; set; }

    [DmColumn("duration_ms", isNullable: false, providerDataType: "{postgresql:bigint}")]
    public long duration_ms { get; set; }
}
