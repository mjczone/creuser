#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Per-workspace plugin settings — a JSON blob keyed on
/// <c>(workspace_id, plugin_id)</c>. Plugins read this at execution
/// time (deserialize to their own typed settings record); the host
/// stores the JSON verbatim. Secrets stay out of this table; plugins
/// store secret filenames here and resolve to values via
/// <c>SecretsService</c>.
/// </summary>
[DmTable("cr", "workspace_plugin_settings")]
[DmPrimaryKeyConstraint(["workspace_id", "plugin_id"])]
[DmIndex(false, ["plugin_id"])]
public class workspace_plugin_settings
{
    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("plugin_id", length: 128, isNullable: false)]
    public string plugin_id { get; set; } = string.Empty;

    [DmColumn("settings", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string settings { get; set; } = "{}";

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
