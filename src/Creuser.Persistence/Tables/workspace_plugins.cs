#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Per-workspace plugin enablement. A plugin loaded by the host is
/// available to a workspace only when there's a row here with
/// <see cref="enabled"/> = true. Composite primary key on
/// <c>(workspace_id, plugin_id)</c> — at most one row per workspace +
/// plugin pair.
/// </summary>
[DmTable("cr", "workspace_plugins")]
[DmPrimaryKeyConstraint(["workspace_id", "plugin_id"])]
[DmIndex(false, ["plugin_id"])]
public class workspace_plugins
{
    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("plugin_id", length: 128, isNullable: false)]
    public string plugin_id { get; set; } = string.Empty;

    [DmColumn("enabled", isNullable: false, defaultExpression: "true")]
    public bool enabled { get; set; } = true;

    [DmColumn(
        "updated_at",
        isNullable: false,
        providerDataType: "{postgresql:timestamptz}",
        defaultExpression: "CURRENT_TIMESTAMP"
    )]
    public DateTime updated_at { get; set; }

    [DmColumn("updated_by", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? updated_by { get; set; }
}
