#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Discovered + activated plugin registry, populated at host startup by
/// <c>PluginInitializer</c>. Refreshed each boot — rows for plugins no
/// longer present on disk get deleted on next startup. Per-workspace
/// enablement lives in <see cref="workspace_plugins"/> and survives
/// re-discovery (joined by <see cref="id"/>).
/// </summary>
[DmTable("cr", "plugins")]
[DmPrimaryKeyConstraint(["id"])]
public class plugins
{
    [DmColumn("id", length: 128, isNullable: false)]
    public string id { get; set; } = string.Empty;

    [DmColumn("name", length: 256, isNullable: false)]
    public string name { get; set; } = string.Empty;

    [DmColumn("version", length: 64, isNullable: false)]
    public string version { get; set; } = string.Empty;

    [DmColumn("author", length: 256, isNullable: true)]
    public string? author { get; set; }

    [DmColumn("description", length: 2048, isNullable: true)]
    public string? description { get; set; }

    [DmColumn("min_host_version", length: 64, isNullable: true)]
    public string? min_host_version { get; set; }

    /// <summary>JSONB array of host-OS tool deps, e.g. <c>["python>=3.12"]</c>.</summary>
    [DmColumn("required_tools", isNullable: true, providerDataType: "{postgresql:jsonb}")]
    public string? required_tools { get; set; }

    /// <summary>JSONB array of contribution hints, e.g. <c>["StepRunner:hello-world"]</c>.</summary>
    [DmColumn("provides", isNullable: true, providerDataType: "{postgresql:jsonb}")]
    public string? provides { get; set; }

    [DmColumn("documentation_url", length: 1024, isNullable: true)]
    public string? documentation_url { get; set; }

    /// <summary>One of <c>loaded</c>, <c>failed</c>.</summary>
    [DmColumn("status", length: 16, isNullable: false)]
    public string status { get; set; } = "loaded";

    [DmColumn("status_message", length: 2048, isNullable: true)]
    public string? status_message { get; set; }

    [DmColumn("loaded_at", isNullable: false, providerDataType: "{postgresql:timestamptz}")]
    public DateTime loaded_at { get; set; }
}
