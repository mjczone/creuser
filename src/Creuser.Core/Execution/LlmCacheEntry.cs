namespace Creuser.Core.Execution;

/// <summary>
/// One cached LLM response, keyed by <see cref="CacheKey"/> = sha256 of
/// (model || prompt || systemPrompt || responseFormatHash || temperature).
/// Independent of the broader step idempotency cache so deterministic steps
/// that internally call LLMs still benefit when the outer step's inputs
/// shift slightly.
///
/// <para>
/// TTL defaults to 30 days (configurable). Replay flavours (cache / soft /
/// hard) gate which lookups happen — see architecture.md "Auditability and
/// replay".
/// </para>
/// </summary>
public sealed record LlmCacheEntry(
    string CacheKey,
    string Provider,
    string Model,
    string PromptHash,
    /// <summary>Stored response body — assistant text + any tool-call sequence as JSON.</summary>
    string ResponseJson,
    long? TokensUsed,
    decimal? CostUsd,
    DateTime CreatedAt,
    DateTime ExpiresAt
);

public interface ILlmCacheStore
{
    Task<LlmCacheEntry?> FindAsync(string cacheKey, CancellationToken ct = default);
    Task SaveAsync(LlmCacheEntry entry, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(DateTime now, CancellationToken ct = default);
}
