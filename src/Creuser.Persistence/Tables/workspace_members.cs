#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Per-workspace access grants. Each row gives one user an Editor or
/// Viewer role on one workspace. Admins do NOT need rows here —
/// admin-ness implies Editor on every workspace per the architecture's
/// auth model. This keeps the table free of the "ghost rows for every
/// admin × workspace" pattern.
/// </summary>
[DmTable("cr", "workspace_members")]
[DmPrimaryKeyConstraint(["workspace_id", "user_id"])]
[DmIndex(false, ["user_id"])]
public class workspace_members
{
    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("user_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid user_id { get; set; }

    /// <summary>One of <c>Editor</c>, <c>Viewer</c>.</summary>
    [DmColumn("role", length: 16, isNullable: false)]
    public string role { get; set; } = "Viewer";

    [DmColumn(
        "granted_at",
        isNullable: false,
        providerDataType: "{postgresql:timestamptz}",
        defaultExpression: "CURRENT_TIMESTAMP"
    )]
    public DateTime granted_at { get; set; }

    [DmColumn("granted_by", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? granted_by { get; set; }
}
