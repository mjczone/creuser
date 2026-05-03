using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Creuser.Agents;
using Creuser.Core.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// First registered <see cref="IStepRunner"/>: a single-shot LLM completion.
/// Inputs declare the prompt (or it comes from the script's body), the
/// optional system prompt, the model override, and the optional response
/// format. Outputs are <c>{ text, tokens_used, cost_usd, model }</c>.
///
/// <para>
/// Caching: keyed by sha256 of (provider || model || prompt || systemPrompt
/// || responseFormatHash || temperature). Hits return the cached response
/// without re-calling the provider; the step's audit record reflects the
/// cache hit (status: succeeded, tokens reported as zero on the LLM side
/// even though the original call's count is still in the cache row).
/// </para>
///
/// <para>
/// No file mutations. No tool calls (this is the deterministic-LLM seam;
/// agentic loops live in <c>llm-tool-loop</c>). Suitable for: structured
/// data extraction, summarization, classification, content generation when
/// the output goes downstream rather than to disk.
/// </para>
/// </summary>
public sealed class LlmChatStepRunner : IStepRunner
{
    public string StepType => "llm-chat";

    private readonly IChatClientResolver _resolver;
    private readonly ILlmCacheStore _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<LlmChatStepRunner> _logger;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromDays(30);

    public LlmChatStepRunner(
        IChatClientResolver resolver,
        ILlmCacheStore cache,
        TimeProvider time,
        ILogger<LlmChatStepRunner> logger
    )
    {
        _resolver = resolver;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        var prompt = GetString(inputs, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            sw.Stop();
            return StepResult.Failure(
                "llm-chat step requires a `prompt` input. For single-step jobs the body of the script is moved into `prompt` automatically; check the executor's binding step.",
                sw.ElapsedMilliseconds
            );
        }

        var systemPrompt = GetString(inputs, "system_prompt");
        var providerOverride = GetString(inputs, "provider");
        var modelOverride = GetString(inputs, "model");
        var temperature = GetFloat(inputs, "temperature");
        var responseFormatJson = GetString(inputs, "response_format_json");

        var resolution = await _resolver.ResolveAsync(providerOverride, modelOverride, ct);
        if (resolution.Client is null)
        {
            sw.Stop();
            return StepResult.Failure(
                resolution.Reason ?? "Failed to resolve a chat client.",
                sw.ElapsedMilliseconds
            );
        }

        var provider = resolution.Provider ?? "unknown";
        var model = resolution.Model ?? "unknown";

        var cacheKey = ComputeCacheKey(
            provider,
            model,
            prompt,
            systemPrompt,
            temperature,
            responseFormatJson
        );

        var cached = await _cache.FindAsync(cacheKey, ct);
        if (cached is not null)
        {
            _logger.LogDebug(
                "llm-chat cache HIT for step {StepName} (key {Key:N})",
                ctx.StepName,
                cacheKey[..Math.Min(8, cacheKey.Length)]
            );
            sw.Stop();
            return BuildResultFromCache(cached, sw.ElapsedMilliseconds);
        }

        // Cache miss — call the provider.
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        var options = new ChatOptions { ModelId = model };
        if (temperature.HasValue)
            options.Temperature = temperature.Value;
        // ResponseFormat is forward-looking — when callers pass a JSON Schema
        // we'll wire ChatResponseFormat.Json with the schema. For v0.1 the
        // step accepts free-text response.

        ChatResponse response;
        try
        {
            response = await resolution.Client.GetResponseAsync(messages, options, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "llm-chat call failed for step {StepName}", ctx.StepName);
            return StepResult.Failure($"LLM call failed: {ex.Message}", sw.ElapsedMilliseconds);
        }

        var text = response.Text ?? string.Empty;
        var tokensUsed = ExtractTokenCount(response);
        // Cost computation is provider/model-specific and lives in a future
        // PricingTable. For v0.1 we report null and let the UI show
        // "tokens: N, cost: —".
        decimal? costUsd = null;

        sw.Stop();

        var responseJson = JsonSerializer.Serialize(
            new
            {
                text,
                tokens_used = tokensUsed,
                cost_usd = costUsd,
                model,
                provider,
            }
        );
        var entry = new LlmCacheEntry(
            CacheKey: cacheKey,
            Provider: provider,
            Model: model,
            PromptHash: Sha256(prompt),
            ResponseJson: responseJson,
            TokensUsed: tokensUsed,
            CostUsd: costUsd,
            CreatedAt: _time.GetUtcNow().UtcDateTime,
            ExpiresAt: _time.GetUtcNow().UtcDateTime + _cacheTtl
        );
        await _cache.SaveAsync(entry, ct);

        var outputs = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["tokens_used"] = tokensUsed,
            ["cost_usd"] = costUsd,
            ["model"] = model,
            ["provider"] = provider,
            ["from_cache"] = false,
        };

        // The full transcript is captured as a sidecar artifact so RunInspector
        // can render it. Single-turn for llm-chat — bigger when llm-tool-loop
        // lands.
        var transcript = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                provider,
                model,
                system = systemPrompt,
                user = prompt,
                assistant = text,
                tokens_used = tokensUsed,
            },
            new JsonSerializerOptions { WriteIndented = true }
        );

        return new StepResult(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts:
            [
                new StepArtifact("transcript", "transcript.json", transcript, "application/json"),
            ],
            DurationMs: sw.ElapsedMilliseconds,
            TokensUsed: tokensUsed,
            CostUsd: costUsd
        );
    }

    private static StepResult BuildResultFromCache(LlmCacheEntry cached, long durationMs)
    {
        // Roll the cached response back into the step's outputs. The cached
        // ResponseJson always carries the same shape this runner produces.
        var doc = JsonDocument.Parse(cached.ResponseJson);
        var root = doc.RootElement;
        var outputs = new Dictionary<string, object?>
        {
            ["text"] = root.TryGetProperty("text", out var t) ? t.GetString() : null,
            ["tokens_used"] = cached.TokensUsed,
            ["cost_usd"] = cached.CostUsd,
            ["model"] = cached.Model,
            ["provider"] = cached.Provider,
            ["from_cache"] = true,
        };
        return new StepResult(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts: Array.Empty<StepArtifact>(),
            DurationMs: durationMs,
            TokensUsed: cached.TokensUsed,
            CostUsd: cached.CostUsd
        );
    }

    private static long? ExtractTokenCount(ChatResponse response)
    {
        // ChatResponse.Usage carries token counts on providers that report
        // them. Sum input + output for the audit total.
        var usage = response.Usage;
        if (usage is null)
            return null;
        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        var total = input + output;
        return total == 0 ? null : total;
    }

    private static string ComputeCacheKey(
        string provider,
        string model,
        string prompt,
        string? systemPrompt,
        float? temperature,
        string? responseFormatJson
    )
    {
        var sb = new StringBuilder();
        sb.Append(provider).Append('|');
        sb.Append(model).Append('|');
        sb.Append(prompt).Append('|');
        sb.Append(systemPrompt ?? "").Append('|');
        sb.Append(
                temperature?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? ""
            )
            .Append('|');
        sb.Append(responseFormatJson ?? "");
        return Sha256(sb.ToString());
    }

    private static string Sha256(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static float? GetFloat(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return null;
        return v switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            string s
                when float.TryParse(
                    s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var f
                ) => f,
            _ => null,
        };
    }
}
