// Lowercase class + lowercase property names match column names exactly so
// Dapper's default mapper can populate rows without snake_case <-> PascalCase
// translation. This is a deliberate convention for DapperMatic-managed
// tables under the `cr` schema. DTOs (request/response, domain models)
// stay PascalCase.
#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

[DmTable("cr", "users")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["email"])]
[DmIndex(false, ["is_active"])]
public class users
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("email", length: 255, isNullable: false)]
    [DmUniqueConstraint]
    public string email { get; set; } = string.Empty;

    [DmColumn("display_name", length: 255, isNullable: false)]
    public string display_name { get; set; } = string.Empty;

    [DmColumn("role", length: 32, isNullable: false, defaultExpression: "'User'")]
    public string role { get; set; } = "User";

    [DmColumn("password_hash", length: 512, isNullable: false)]
    public string password_hash { get; set; } = string.Empty;

    [DmColumn("is_active", isNullable: false, defaultExpression: "true")]
    public bool is_active { get; set; } = true;

    [DmColumn("must_change_password", isNullable: false, defaultExpression: "false")]
    public bool must_change_password { get; set; }

    [DmColumn("last_login_at", isNullable: true, providerDataType: "{postgresql:timestamptz}")]
    public DateTime? last_login_at { get; set; }

    [DmColumn(
        "password_changed_at",
        isNullable: true,
        providerDataType: "{postgresql:timestamptz}"
    )]
    public DateTime? password_changed_at { get; set; }

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
}
