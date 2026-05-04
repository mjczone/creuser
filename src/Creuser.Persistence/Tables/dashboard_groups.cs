#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// UI grouping for dashboards in the workspace icon bar — each group is one
/// icon that opens the sub-sidebar listing its dashboards. Groups are
/// admin-curated; the dashboards they contain are otherwise ordinary.
/// Group membership is the optional <c>cr.dashboards.group_id</c> FK; the
/// group is purely a sidebar arrangement.
/// </summary>
[DmTable("cr", "dashboard_groups")]
[DmPrimaryKeyConstraint(["id"])]
[DmUniqueConstraint(["workspace_id", "slug"])]
[DmIndex(false, ["workspace_id", "position"])]
public class dashboard_groups
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("slug", length: 64, isNullable: false)]
    public string slug { get; set; } = string.Empty;

    [DmColumn("name", length: 128, isNullable: false)]
    public string name { get; set; } = string.Empty;

    /// <summary>Material icon name (e.g. <c>bolt</c>, <c>analytics</c>).</summary>
    [DmColumn("icon", length: 64, isNullable: false)]
    public string icon { get; set; } = "folder";

    [DmColumn("position", isNullable: false, defaultExpression: "0")]
    public int position { get; set; }

    /// <summary>True when shipped by the seeder; protects from hard-delete.</summary>
    [DmColumn("is_default", isNullable: false, defaultExpression: "false")]
    public bool is_default { get; set; }

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
