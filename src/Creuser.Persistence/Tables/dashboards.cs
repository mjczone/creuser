#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// One saved dashboard scoped to a workspace. A dashboard is a serialized
/// dockview-vue layout (<c>layout</c> JSONB) plus a list of widget instances
/// (<c>widgets</c> JSONB) that the layout's panels reference by id. The
/// dashboard slug is the URL-stable identifier (<c>/w/:slug/d/:dashboardSlug</c>);
/// optional <c>group_id</c> places it under a sidebar group, otherwise it's
/// standalone with its own icon-bar entry.
/// </summary>
[DmTable("cr", "dashboards")]
[DmPrimaryKeyConstraint(["id"])]
[DmUniqueConstraint(["workspace_id", "slug"])]
[DmIndex(false, ["workspace_id", "position"])]
[DmIndex(false, ["workspace_id", "group_id", "position"])]
public class dashboards
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    /// <summary>FK to <c>cr.dashboard_groups.id</c>; null = standalone (own icon-bar entry).</summary>
    [DmColumn("group_id", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? group_id { get; set; }

    [DmColumn("slug", length: 64, isNullable: false)]
    public string slug { get; set; } = string.Empty;

    [DmColumn("name", length: 128, isNullable: false)]
    public string name { get; set; } = string.Empty;

    /// <summary>Material icon name. Required for standalone, optional inside a group.</summary>
    [DmColumn("icon", length: 64, isNullable: true)]
    public string? icon { get; set; }

    /// <summary>Serialized dockview-vue <c>SerializedDockview</c>. Empty object on first save.</summary>
    [DmColumn("layout", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string layout { get; set; } = "{}";

    /// <summary>Array of <c>{ id, widgetType, props }</c> objects keyed by panels in <c>layout</c>.</summary>
    [DmColumn("widgets", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string widgets { get; set; } = "[]";

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
