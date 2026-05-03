using Creuser.Agents;
using Creuser.Persistence.AppSettings;
using Creuser.Web.Endpoints;
using Creuser.Web.Environment;
using Microsoft.Extensions.AI;

namespace Creuser.Web.Agents;

/// <summary>
/// Bridges Environment config + on-disk secrets to the provider-agnostic
/// <see cref="AgentClientFactory"/>. Callers that need an
/// <see cref="IChatClient"/> for the configured deployment ask this
/// resolver — it loads the saved <see cref="EnvironmentConfig"/>, pulls
/// the API key from <see cref="SecretsService"/>, and delegates the actual
/// client construction.
/// </summary>
public sealed class AgentClientResolver : IChatClientResolver
{
    private readonly IAppSettingsStore _settings;
    private readonly SecretsService _secrets;
    private readonly AgentClientFactory _factory;

    public AgentClientResolver(
        IAppSettingsStore settings,
        SecretsService secrets,
        AgentClientFactory factory
    )
    {
        _settings = settings;
        _secrets = secrets;
        _factory = factory;
    }

    /// <summary>
    /// <see cref="IChatClientResolver"/> contract — adapts the more
    /// specific <see cref="ResolveAsync(string?, string?, CancellationToken)"/>
    /// outcome to the cross-layer shape that <c>Creuser.Scripting</c>
    /// (and future plugins) consume.
    /// </summary>
    async Task<ChatClientResolution> IChatClientResolver.ResolveAsync(
        string? provider,
        string? modelOverride,
        CancellationToken ct
    )
    {
        var outcome = await ResolveAsync(provider, modelOverride, useFunctionInvocation: true, ct);
        return new ChatClientResolution(
            Client: outcome.Client?.Client,
            Provider: outcome.Client?.Provider,
            Model: outcome.Client?.Model,
            Reason: outcome.Reason
        );
    }

    async Task<ChatClientResolution> IChatClientResolver.ResolveRawAsync(
        string? provider,
        string? modelOverride,
        CancellationToken ct
    )
    {
        var outcome = await ResolveAsync(provider, modelOverride, useFunctionInvocation: false, ct);
        return new ChatClientResolution(
            Client: outcome.Client?.Client,
            Provider: outcome.Client?.Provider,
            Model: outcome.Client?.Model,
            Reason: outcome.Reason
        );
    }

    public sealed record ResolvedClient(
        string Provider,
        string Model,
        string? BaseUrl,
        IChatClient Client
    );

    /// <summary>
    /// Outcome of a <see cref="ResolveAsync"/> call. Either <see cref="Client"/>
    /// is non-null and the resolution succeeded, or <see cref="Reason"/>
    /// carries a human-readable explanation of what's missing (no API key,
    /// no model, unknown provider, etc.) so callers can surface a useful
    /// message instead of a generic "not configured".
    /// </summary>
    public sealed record ResolveOutcome(ResolvedClient? Client, string? Reason);

    /// <summary>
    /// Resolve an <see cref="IChatClient"/> for a specific provider, or for
    /// the configured default when <paramref name="provider"/> is null.
    /// </summary>
    public Task<ResolveOutcome> ResolveAsync(
        string? provider = null,
        string? modelOverride = null,
        CancellationToken ct = default
    ) => ResolveAsync(provider, modelOverride, useFunctionInvocation: true, ct);

    /// <summary>
    /// Same as <see cref="ResolveAsync(string?, string?, CancellationToken)"/>
    /// but lets the caller suppress the <c>UseFunctionInvocation()</c>
    /// middleware. The tool-loop runner uses this so it can drive its own
    /// ReAct loop with explicit per-turn budget enforcement.
    /// </summary>
    public async Task<ResolveOutcome> ResolveAsync(
        string? provider,
        string? modelOverride,
        bool useFunctionInvocation,
        CancellationToken ct = default
    )
    {
        var env =
            await _settings.GetAsync<EnvironmentConfig>(EnvironmentEndpoints.SettingKey, ct)
            ?? EnvironmentConfig.Default;

        var resolvedProvider = (
            provider ?? env.AiProviders.DefaultProvider ?? "anthropic"
        ).ToLowerInvariant();

        return resolvedProvider switch
        {
            "openai" => await ResolveOpenAIAsync(
                env.AiProviders.OpenAI,
                modelOverride,
                useFunctionInvocation,
                ct
            ),
            "anthropic" => await ResolveAnthropicAsync(
                env.AiProviders.Anthropic,
                modelOverride,
                useFunctionInvocation,
                ct
            ),
            "local" => await ResolveLocalAsync(
                env.AiProviders.Local,
                modelOverride,
                useFunctionInvocation,
                ct
            ),
            _ => Missing($"Unknown provider '{resolvedProvider}'."),
        };
    }

    private async Task<ResolveOutcome> ResolveLocalAsync(
        LocalProviderConfig? config,
        string? modelOverride,
        bool useFunctionInvocation,
        CancellationToken ct
    )
    {
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
            return Missing(
                "Local provider has no endpoint URL. Pick a preset or enter the server's "
                    + "base URL (e.g. http://localhost:11434/v1 for Ollama)."
            );

        var model = modelOverride ?? config.DefaultModel;
        if (string.IsNullOrWhiteSpace(model))
            return Missing(
                "Local provider has no model selected. Set the Model field to one your local "
                    + "server has loaded (e.g. `llama3.1`)."
            );

        // API key is optional for local providers — Ollama / LM Studio don't
        // authenticate by default. When unset, pass a placeholder so the
        // OpenAI SDK's credential check passes.
        var apiKey = "local";
        if (!string.IsNullOrWhiteSpace(config.ApiKeySecret))
        {
            var loaded = await _secrets.ReadInternalAsync(config.ApiKeySecret, ct);
            if (!string.IsNullOrWhiteSpace(loaded))
                apiKey = loaded;
        }

        // "local" is OpenAI-wire-compatible — same factory path, custom endpoint.
        var client = _factory.Create(
            "openai",
            apiKey,
            model,
            config.BaseUrl,
            useFunctionInvocation
        );
        return client is null
            ? Missing("Failed to construct the local OpenAI-compatible client.")
            : Resolved(new ResolvedClient("local", model, config.BaseUrl, client));
    }

    private async Task<ResolveOutcome> ResolveAnthropicAsync(
        AnthropicConfig? config,
        string? modelOverride,
        bool useFunctionInvocation,
        CancellationToken ct
    )
    {
        var keyName = config?.ApiKeySecret ?? "anthropic.key";
        var apiKey = await _secrets.ReadInternalAsync(keyName, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            return Missing(
                $"Anthropic API key isn't set. Save a value into Settings → Environment → "
                    + $"AI providers → Anthropic (stored at /data/secrets/{keyName})."
            );

        var model = modelOverride ?? config?.DefaultModel ?? "claude-opus-4-7";
        var client = _factory.Create(
            "anthropic",
            apiKey,
            model,
            config?.BaseUrl,
            useFunctionInvocation
        );
        return client is null
            ? Missing("Failed to construct the Anthropic client.")
            : Resolved(new ResolvedClient("anthropic", model, config?.BaseUrl, client));
    }

    private async Task<ResolveOutcome> ResolveOpenAIAsync(
        OpenAIConfig? config,
        string? modelOverride,
        bool useFunctionInvocation,
        CancellationToken ct
    )
    {
        var keyName = config?.ApiKeySecret ?? "openai.key";
        var apiKey = await _secrets.ReadInternalAsync(keyName, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            return Missing(
                $"OpenAI API key isn't set. Save a value into Settings → Environment → "
                    + $"AI providers → OpenAI (stored at /data/secrets/{keyName})."
            );

        var model = modelOverride ?? config?.DefaultModel ?? "gpt-5";
        var client = _factory.Create(
            "openai",
            apiKey,
            model,
            config?.BaseUrl,
            useFunctionInvocation
        );
        return client is null
            ? Missing("Failed to construct the OpenAI client.")
            : Resolved(new ResolvedClient("openai", model, config?.BaseUrl, client));
    }

    private static ResolveOutcome Missing(string reason) => new(null, reason);

    private static ResolveOutcome Resolved(ResolvedClient client) => new(client, null);
}
