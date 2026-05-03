// Lowercase class + lowercase property names match column names exactly so
// Dapper's default mapper can populate rows without snake_case <-> PascalCase
// translation. See Tables/users.cs for the convention rationale.
#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Singleton platform configuration as key/JSONB rows. Each well-known key
/// (`branding`, `smtp`, `ai-providers`, ...) corresponds to a typed record
/// on the C# side, serialized to/from the <c>value</c> column as JSON.
/// </summary>
[DmTable("cr", "app_settings")]
[DmPrimaryKeyConstraint(["key"])]
public class app_settings
{
    [DmColumn("key", length: 64, isNullable: false)]
    public string key { get; set; } = string.Empty;

    [DmColumn("value", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string value { get; set; } = "{}";

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
