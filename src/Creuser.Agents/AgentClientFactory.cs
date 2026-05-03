using Anthropic.SDK;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Creuser.Agents;

/// <summary>
/// Provider-agnostic factory for <see cref="IChatClient"/> instances. Each
/// `Create*` method takes an API key + model id (+ optional base URL
/// override) and returns a Microsoft.Extensions.AI <see cref="IChatClient"/>
/// that the rest of the app talks to. Higher-level services in
/// <c>Creuser.Web</c> resolve the config + secrets, then delegate here for
/// the actual client construction.
/// </summary>
public sealed class AgentClientFactory
{
    /// <summary>
    /// Construct a chat client for the named provider. Returns <c>null</c>
    /// if the provider is unknown or required parameters are missing.
    ///
    /// <para>
    /// <paramref name="useFunctionInvocation"/> defaults to <c>true</c>.
    /// Set to <c>false</c> when the caller drives its own ReAct loop —
    /// e.g. <c>LlmToolLoopStepRunner</c> needs explicit per-turn budget
    /// enforcement and tool-log recording, which the auto-invocation
    /// middleware would short-circuit.
    /// </para>
    /// </summary>
    public IChatClient? Create(
        string provider,
        string apiKey,
        string model,
        string? baseUrl = null,
        bool useFunctionInvocation = true
    )
    {
        if (
            string.IsNullOrWhiteSpace(provider)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(model)
        )
            return null;

        return provider.ToLowerInvariant() switch
        {
            "openai" => CreateOpenAI(apiKey, model, baseUrl, useFunctionInvocation),
            "anthropic" => CreateAnthropic(apiKey, model, baseUrl, useFunctionInvocation),
            _ => null,
        };
    }

    private static IChatClient CreateOpenAI(
        string apiKey,
        string model,
        string? baseUrl,
        bool useFunctionInvocation
    )
    {
        // The OpenAI .NET SDK is the official client; M.E.AI.OpenAI's
        // `AsIChatClient()` extension wraps an OpenAI ChatClient as an
        // M.E.AI IChatClient. The same path works for Azure OpenAI when an
        // override base URL is provided. AND for OpenAI-compatible local
        // servers (Ollama, LM Studio, vLLM) — same factory routes there
        // when the resolver hands us the local config's BaseUrl.
        //
        // `UseFunctionInvocation()` is critical: without it, the runtime
        // returns tool_call responses raw to the caller without ever
        // executing the tool. Gemma / GPT / etc. all correctly emit tool
        // calls; this is what runs the loop.
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl))
            options.Endpoint = new Uri(baseUrl);

        var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), options);
        var builder = client.GetChatClient(model).AsIChatClient().AsBuilder();
        if (useFunctionInvocation)
            builder = builder.UseFunctionInvocation();
        return builder.Build();
    }

    private static IChatClient CreateAnthropic(
        string apiKey,
        string model,
        string? baseUrl,
        bool useFunctionInvocation
    )
    {
        // Anthropic.SDK exposes IChatClient via Messages.AsBuilder(). Note:
        // the `model` argument is unused at construction time — Anthropic.SDK
        // uses model on per-request options. Setting a default via
        // `ChatOptions.ModelId` happens in the calling code (see
        // AgentsEndpoints.Health for an example).
        //
        // Bedrock / proxy support via custom base URL is on the deferred
        // list — Anthropic.SDK 5.x routes it through a custom HttpClient
        // (with a BaseAddress override) which deserves a more careful
        // wrapper than this v1 factory.
        _ = baseUrl;
        _ = model;
        var anthropic = new AnthropicClient(new APIAuthentication(apiKey));
        var builder = anthropic.Messages.AsBuilder();
        if (useFunctionInvocation)
            builder = builder.UseFunctionInvocation();
        return builder.Build();
    }
}
