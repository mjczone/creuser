#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// One trigger configuration for a job — cron expression or post-sync hook.
/// See architecture.md "Scheduling" for the model.
/// </summary>
[DmTable("cr", "schedules")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["workspace_id", "kind"])]
[DmIndex(false, ["job_script_id"])]
// Index for the scheduler tick — find all due cron schedules cheaply.
[DmIndex(false, ["enabled", "kind", "next_due_at"])]
public class schedules
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("job_script_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid job_script_id { get; set; }

    /// <summary>One of <c>cron</c>, <c>sync</c>.</summary>
    [DmColumn("kind", length: 16, isNullable: false)]
    public string kind { get; set; } = "cron";

    /// <summary>NCrontab expression for cron schedules; null for sync.</summary>
    [DmColumn("cron_expression", length: 128, isNullable: true)]
    public string? cron_expression { get; set; }

    [DmColumn("enabled", isNullable: false, defaultExpression: "true")]
    public bool enabled { get; set; } = true;

    [DmColumn("next_due_at", isNullable: true, providerDataType: "{postgresql:timestamptz}")]
    public DateTime? next_due_at { get; set; }

    [DmColumn("last_fired_at", isNullable: true, providerDataType: "{postgresql:timestamptz}")]
    public DateTime? last_fired_at { get; set; }

    [DmColumn("last_run_id", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? last_run_id { get; set; }

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
