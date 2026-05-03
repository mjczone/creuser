#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Execution;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class llmCacheRepository : ILlmCacheStore
{
    private const string SchemaTable = "cr.llm_cache";
    private readonly NpgsqlDataSource _ds;

    public llmCacheRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<LlmCacheEntry?> FindAsync(string cacheKey, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<llm_cache>(
            new CommandDefinition(
                $"""
                SELECT * FROM {SchemaTable}
                WHERE cache_key = @cacheKey AND expires_at > CURRENT_TIMESTAMP
                LIMIT 1
                """,
                new { cacheKey },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task SaveAsync(LlmCacheEntry entry, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable}
                  (cache_key, provider, model, prompt_hash, response, tokens_used, cost_usd, created_at, expires_at)
                VALUES
                  (@cache_key, @provider, @model, @prompt_hash, @response::jsonb, @tokens_used, @cost_usd, @created_at, @expires_at)
                ON CONFLICT (cache_key) DO UPDATE SET
                  provider     = EXCLUDED.provider,
                  model        = EXCLUDED.model,
                  prompt_hash  = EXCLUDED.prompt_hash,
                  response     = EXCLUDED.response,
                  tokens_used  = EXCLUDED.tokens_used,
                  cost_usd     = EXCLUDED.cost_usd,
                  expires_at   = EXCLUDED.expires_at
                """,
                ToRow(entry),
                cancellationToken: ct
            )
        );
    }

    public async Task<int> PurgeExpiredAsync(DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            new CommandDefinition(
                $"DELETE FROM {SchemaTable} WHERE expires_at <= @now",
                new { now },
                cancellationToken: ct
            )
        );
    }

    private static LlmCacheEntry ToDomain(llm_cache r) =>
        new(
            r.cache_key,
            r.provider,
            r.model,
            r.prompt_hash,
            r.response,
            r.tokens_used,
            r.cost_usd,
            r.created_at,
            r.expires_at
        );

    private static llm_cache ToRow(LlmCacheEntry e) =>
        new()
        {
            cache_key = e.CacheKey,
            provider = e.Provider,
            model = e.Model,
            prompt_hash = e.PromptHash,
            response = e.ResponseJson,
            tokens_used = e.TokensUsed,
            cost_usd = e.CostUsd,
            created_at = e.CreatedAt,
            expires_at = e.ExpiresAt,
        };
}
