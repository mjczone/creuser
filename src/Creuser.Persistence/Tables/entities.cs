#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Generic projection table populated from each workspace's working tree on
/// every successful sync. One row per file matched by a convention; the
/// <c>(workspace_id, kind, slug)</c> triple is unique. See
/// <c>docs/wip/projections-design.md</c> for the storage rationale.
/// </summary>
[DmTable("cr", "entities")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["workspace_id", "kind"])]
[DmIndex(false, ["workspace_id", "path"])]
[DmUniqueConstraint(["workspace_id", "kind", "slug"])]
public class entities
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    /// <summary>Entity kind — the convention's <c>id</c>. Workspace-scoped namespace.</summary>
    [DmColumn("kind", length: 128, isNullable: false)]
    public string kind { get; set; } = string.Empty;

    /// <summary>Stable identifier within the kind. Together with <see cref="workspace_id"/> + <see cref="kind"/> uniquely identifies the entity.</summary>
    [DmColumn("slug", length: 256, isNullable: false)]
    public string slug { get; set; } = string.Empty;

    /// <summary>Path relative to the workspace root, forward slashes.</summary>
    [DmColumn("path", length: 1024, isNullable: false)]
    public string path { get; set; } = string.Empty;

    /// <summary>Convention id that produced this row.</summary>
    [DmColumn("convention_id", length: 128, isNullable: false)]
    public string convention_id { get; set; } = string.Empty;

    /// <summary>Frontmatter + computed fields, merged. JSONB.</summary>
    [DmColumn("metadata", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string metadata { get; set; } = "{}";

    /// <summary>sha256 of the file contents at scan time.</summary>
    [DmColumn("content_hash", length: 64, isNullable: false)]
    public string content_hash { get; set; } = string.Empty;

    [DmColumn(
        "last_seen_at",
        isNullable: false,
        providerDataType: "{postgresql:timestamptz}",
        defaultExpression: "CURRENT_TIMESTAMP"
    )]
    public DateTime last_seen_at { get; set; }
}
