#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Per-step audit record within a <see cref="job_runs"/>. The structural
/// unit of replay — see architecture.md "Execution model → Auditability".
/// </summary>
[DmTable("cr", "job_run_steps")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["run_id", "position"])]
[DmIndex(false, ["idempotency_key"])]
public class job_run_steps
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("run_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid run_id { get; set; }

    /// <summary>Order within the run. Same as DAG topological ordering for deterministic runs.</summary>
    [DmColumn("position", isNullable: false, providerDataType: "{postgresql:int}")]
    public int position { get; set; }

    [DmColumn("step_type", length: 64, isNullable: false)]
    public string step_type { get; set; } = string.Empty;

    [DmColumn("name", length: 255, isNullable: false)]
    public string name { get; set; } = string.Empty;

    /// <summary>One of <c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>skipped</c>, <c>paused</c>, <c>failed</c>, <c>cancelled</c>.</summary>
    [DmColumn("status", length: 16, isNullable: false)]
    public string status { get; set; } = "pending";

    /// <summary>sha256 of (stepType || normalized(inputs) || stepConfigHash). Drives the skip-on-prior-success cache.</summary>
    [DmColumn("idempotency_key", length: 128, isNullable: false)]
    public string idempotency_key { get; set; } = string.Empty;

    /// <summary>When status = skipped, FK to the prior <see cref="job_run_steps"/> whose outputs were inherited.</summary>
    [DmColumn("cached_from_step_id", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? cached_from_step_id { get; set; }

    [DmColumn("inputs", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string inputs { get; set; } = "{}";

    [DmColumn("outputs", isNullable: true, providerDataType: "{postgresql:jsonb}")]
    public string? outputs { get; set; }

    /// <summary>sha256 of canonicalized inputs JSON, indexed for fast cache lookups.</summary>
    [DmColumn("inputs_hash", length: 64, isNullable: false)]
    public string inputs_hash { get; set; } = string.Empty;

    [DmColumn("file_change_count", isNullable: false, providerDataType: "{postgresql:int}")]
    public int file_change_count { get; set; }

    [DmColumn("commit_sha", length: 64, isNullable: true)]
    public string? commit_sha { get; set; }

    [DmColumn("started_at", isNullable: false, providerDataType: "{postgresql:timestamptz}")]
    public DateTime started_at { get; set; }

    [DmColumn("completed_at", isNullable: true, providerDataType: "{postgresql:timestamptz}")]
    public DateTime? completed_at { get; set; }

    [DmColumn("duration_ms", isNullable: false, providerDataType: "{postgresql:bigint}")]
    public long duration_ms { get; set; }

    [DmColumn("tokens_used", isNullable: true, providerDataType: "{postgresql:bigint}")]
    public long? tokens_used { get; set; }

    [DmColumn("cost_usd", isNullable: true, providerDataType: "{postgresql:numeric(10, 4)}")]
    public decimal? cost_usd { get; set; }

    [DmColumn("error_message", length: 4096, isNullable: true)]
    public string? error_message { get; set; }

    [DmColumn("resume_token", length: 1024, isNullable: true)]
    public string? resume_token { get; set; }
}
