#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// Typed edges between entities — the relationship table that turns
/// <c>cr.entities</c> into a knowledge graph. <c>to_entity_id</c> is
/// nullable: refs that don't resolve persist with the raw target preserved
/// so <c>find_unresolved_refs</c> can surface gaps without a re-scan.
/// </summary>
[DmTable("cr", "entity_refs")]
[DmPrimaryKeyConstraint(["id"])]
[DmIndex(false, ["from_entity_id"])]
[DmIndex(false, ["to_entity_id"])]
[DmIndex(false, ["workspace_id", "target_kind", "target_slug"])]
public class entity_refs
{
    [DmColumn("id", providerDataType: "{postgresql:uuid}", defaultExpression: "gen_random_uuid()")]
    public Guid id { get; set; }

    [DmColumn("workspace_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid workspace_id { get; set; }

    [DmColumn("from_entity_id", isNullable: false, providerDataType: "{postgresql:uuid}")]
    public Guid from_entity_id { get; set; }

    /// <summary>Resolved target. Null when the ref didn't resolve — see <see cref="target_kind"/> / <see cref="target_slug"/>.</summary>
    [DmColumn("to_entity_id", isNullable: true, providerDataType: "{postgresql:uuid}")]
    public Guid? to_entity_id { get; set; }

    /// <summary>Edge label. e.g. <c>parent</c>, <c>references</c>, <c>implements</c>.</summary>
    [DmColumn("relationship", length: 64, isNullable: false)]
    public string relationship { get; set; } = string.Empty;

    /// <summary>Preserved target kind for unresolved refs.</summary>
    [DmColumn("target_kind", length: 128, isNullable: true)]
    public string? target_kind { get; set; }

    /// <summary>Preserved target slug for unresolved refs.</summary>
    [DmColumn("target_slug", length: 256, isNullable: true)]
    public string? target_slug { get; set; }

    /// <summary>Optional JSONB edge metadata — line numbers, source-of-ref, etc.</summary>
    [DmColumn("metadata", isNullable: true, providerDataType: "{postgresql:jsonb}")]
    public string? metadata { get; set; }
}
