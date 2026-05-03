#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981 // naming convention

using MJCZone.DapperMatic.DataAnnotations;

namespace Creuser.Persistence.Tables;

/// <summary>
/// LLM response cache, keyed by sha256 of (model || prompt || systemPrompt
/// || responseFormatHash || temperature). Independent of step idempotency
/// so deterministic steps containing LLM sub-calls cache the inner call
/// even when their outer inputs shift slightly.
/// </summary>
[DmTable("cr", "llm_cache")]
[DmPrimaryKeyConstraint(["cache_key"])]
[DmIndex(false, ["expires_at"])]
public class llm_cache
{
    [DmColumn("cache_key", length: 128, isNullable: false)]
    public string cache_key { get; set; } = string.Empty;

    [DmColumn("provider", length: 32, isNullable: false)]
    public string provider { get; set; } = string.Empty;

    [DmColumn("model", length: 128, isNullable: false)]
    public string model { get; set; } = string.Empty;

    [DmColumn("prompt_hash", length: 64, isNullable: false)]
    public string prompt_hash { get; set; } = string.Empty;

    [DmColumn("response", isNullable: false, providerDataType: "{postgresql:jsonb}")]
    public string response { get; set; } = "{}";

    [DmColumn("tokens_used", isNullable: true, providerDataType: "{postgresql:bigint}")]
    public long? tokens_used { get; set; }

    [DmColumn("cost_usd", isNullable: true, providerDataType: "{postgresql:numeric(10, 4)}")]
    public decimal? cost_usd { get; set; }

    [DmColumn(
        "created_at",
        isNullable: false,
        providerDataType: "{postgresql:timestamptz}",
        defaultExpression: "CURRENT_TIMESTAMP"
    )]
    public DateTime created_at { get; set; }

    [DmColumn("expires_at", isNullable: false, providerDataType: "{postgresql:timestamptz}")]
    public DateTime expires_at { get; set; }
}
