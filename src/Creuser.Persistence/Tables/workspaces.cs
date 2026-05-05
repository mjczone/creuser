// Lowercase class + lowercase property names match column names exactly so
// Dapper's default mapper can populate rows without snake_case <-> PascalCase
// translation. See Tables/users.cs for the convention rationale.
#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Configured repository connection — a git repo, S3 bucket, or local
/// directory the platform can read from and (optionally) write to. Slug is
/// the URL-safe identifier (used in /w/:slug/... routes when those land);
/// settings is a type-specific JSONB blob whose schema matches the C#
/// settings record for the workspace's <c>type</c>.
/// </summary>
[DmTable("cr", "workspaces")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["type"])]
public class workspaces
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("slug", length: 64, isNullable: false)]
    [DmUniqueConstraint]
    public string slug { get; set; } = string.Empty;

    [DmColumn("name", length: 255, isNullable: false)]
    public string name { get; set; } = string.Empty;

    [DmColumn("description", length: 1024, isNullable: true)]
    public string? description { get; set; }

    /// <summary>One of <c>git</c>, <c>s3</c>, <c>local</c>.</summary>
    [DmColumn("type", length: 32, isNullable: false)]
    public string type { get; set; } = "git";

    /// <summary>Type-specific configuration JSON. Shape matches the C# settings record (e.g. <c>GitWorkspaceSettings</c>).</summary>
    [DmColumn("settings", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string settings { get; set; } = "{}";

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

    /// <summary>UTC timestamp of the last sync attempt — success or failure.</summary>
    [DmColumn("last_sync_at", isNullable: true, providerDataType: "{postgresql:timestamptz}")]
    public DateTime? last_sync_at { get; set; }

    /// <summary>Resolved commit SHA after the last successful sync. Null on failures or for non-git types.</summary>
    [DmColumn("last_sync_sha", length: 64, isNullable: true)]
    public string? last_sync_sha { get; set; }

    /// <summary>One of <c>ok</c>, <c>failed</c>, <c>never</c>. Drives the UI's status indicator.</summary>
    [DmColumn("last_sync_status", length: 16, isNullable: true)]
    public string? last_sync_status { get; set; }

    /// <summary>Free-text message from the last sync — git stderr on failure, "fast-forwarded N commits" on success.</summary>
    [DmColumn("last_sync_message", length: 2048, isNullable: true)]
    public string? last_sync_message { get; set; }

    /// <summary>UTC timestamp of the last push attempt — success or failure.</summary>
    [DmColumn("last_push_at", isNullable: true, providerDataType: "{postgresql:timestamptz}")]
    public DateTime? last_push_at { get; set; }

    /// <summary>HEAD SHA at the time of the last successful push. Null on failure or for non-git types.</summary>
    [DmColumn("last_push_sha", length: 64, isNullable: true)]
    public string? last_push_sha { get; set; }

    /// <summary>One of <c>ok</c>, <c>nothing-to-push</c>, <c>failed</c>, or null (never pushed).</summary>
    [DmColumn("last_push_status", length: 16, isNullable: true)]
    public string? last_push_status { get; set; }

    /// <summary>Free-text message from the last push — git stderr on failure, "Pushed N commit(s) to origin/&lt;branch&gt;" on success.</summary>
    [DmColumn("last_push_message", length: 2048, isNullable: true)]
    public string? last_push_message { get; set; }
}
