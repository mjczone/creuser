using System.Diagnostics;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Web.Agents;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;

namespace Creuser.Web.Endpoints;

public sealed record AgentHealthResult(
    bool Ok,
    string? Provider,
    string? Model,
    long? LatencyMs,
    string? Reply,
    string? Error
);

public sealed record ChatTurn(string Role, string Content);

public sealed record AgentChatRequest(
    string Message,
    List<ChatTurn>? History = null,
    string? Provider = null,
    /// <summary>SPA route the user is currently viewing — feeds into the system prompt as context.</summary>
    string? CurrentScreen = null
);

public sealed record AgentChatResult(
    bool Ok,
    string Reply,
    string? Provider,
    string? Model,
    long? LatencyMs,
    string? Error
);

public static class AgentsEndpoints
{
    public static IEndpointRouteBuilder MapAgentsEndpoints(this IEndpointRouteBuilder app)
    {
        // Auth scope: any authenticated user, not just Admin. The whole
        // point of in-app AI assistance is to help operators on the screens
        // they're already permitted to view — gating it to Admin would
        // contradict that. Admin-only endpoints under this group still
        // declare their own .RequireAuthorization() (see /health).
        var group = app.MapGroup("/api/agents").WithTags("Agents").RequireAuthorization();

        // GET /api/agents/health?provider=anthropic
        // Sends a tiny prompt to confirm the configured provider+model+key
        // round-trips. Costs a token or two per check; admin-gated. Returns
        // 200 with `ok: false` + an error message rather than 4xx/5xx so the
        // UI can render the failure inline next to its provider section.
        group
            .MapGet("/health", (Delegate)Health)
            .WithName("CheckAgentHealth")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // POST /api/agents/chat
        // v0: non-streaming Q&A with the configured provider. Body carries
        // the user's new message + optional history. The server only sends
        // exactly what's in the body to the LLM — no system prompt, no
        // server-side context, no auto-attached secrets. Future versions
        // will add a curated system prompt + per-screen context + a tool
        // registry, all hand-controlled to keep the exfiltration surface
        // minimal.
        group.MapPost("/chat", (Delegate)Chat).WithName("AgentChat");

        return app;
    }

    private static async Task<Ok<ApiResult<AgentHealthResult>>> Health(
        AgentClientResolver resolver,
        string? provider,
        CancellationToken ct
    )
    {
        var outcome = await resolver.ResolveAsync(provider, ct: ct);
        if (outcome.Client is null)
            return TypedResults.Ok(
                new ApiResult<AgentHealthResult>(
                    new AgentHealthResult(
                        Ok: false,
                        Provider: provider,
                        Model: null,
                        LatencyMs: null,
                        Reply: null,
                        Error: outcome.Reason ?? "Provider isn't configured."
                    )
                )
            );

        var resolved = outcome.Client;
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await resolved.Client.GetResponseAsync(
                "Reply with just the word: pong",
                // Don't set Temperature — newer Claude models (Opus 4.7+
                // extended-thinking class) reject it as deprecated. The
                // health probe is short enough that determinism isn't worth
                // the compatibility tax.
                new ChatOptions { ModelId = resolved.Model, MaxOutputTokens = 16 },
                ct
            );
            sw.Stop();

            var reply = response.Text?.Trim() ?? string.Empty;
            return TypedResults.Ok(
                new ApiResult<AgentHealthResult>(
                    new AgentHealthResult(
                        Ok: true,
                        Provider: resolved.Provider,
                        Model: resolved.Model,
                        LatencyMs: sw.ElapsedMilliseconds,
                        Reply: reply,
                        Error: null
                    )
                )
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return TypedResults.Ok(
                new ApiResult<AgentHealthResult>(
                    new AgentHealthResult(
                        Ok: false,
                        Provider: resolved.Provider,
                        Model: resolved.Model,
                        LatencyMs: sw.ElapsedMilliseconds,
                        Reply: null,
                        // The provider's exception messages tend to be useful
                        // ("incorrect api key", "model not found") — surface
                        // them verbatim so admins can self-diagnose.
                        Error: ex.Message
                    )
                )
            );
        }
    }

    private static async Task<Ok<ApiResult<AgentChatResult>>> Chat(
        AgentChatRequest request,
        AgentClientResolver resolver,
        AgentTools tools,
        HttpContext http,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return TypedResults.Ok(
                new ApiResult<AgentChatResult>(
                    new AgentChatResult(
                        Ok: false,
                        Reply: string.Empty,
                        Provider: null,
                        Model: null,
                        LatencyMs: null,
                        Error: "Message cannot be empty."
                    )
                )
            );

        var outcome = await resolver.ResolveAsync(request.Provider, ct: ct);
        if (outcome.Client is null)
            return TypedResults.Ok(
                new ApiResult<AgentChatResult>(
                    new AgentChatResult(
                        Ok: false,
                        Reply: string.Empty,
                        Provider: request.Provider,
                        Model: null,
                        LatencyMs: null,
                        Error: outcome.Reason ?? "AI provider isn't configured."
                    )
                )
            );

        var resolved = outcome.Client;

        // Build the conversation. System prompt carries explicit screen
        // context + role; nothing introspected, nothing from /data/secrets/.
        // Whitelist principle for what gets sent to the LLM.
        var role = http.User.IsInRole(Roles.Admin) ? Roles.Admin : Roles.User;
        var capCtx = new CapabilityContext(Role: role, CurrentScreen: request.CurrentScreen);

        var conversation = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(capCtx)),
        };
        if (request.History is not null)
        {
            foreach (var turn in request.History)
            {
                if (string.IsNullOrWhiteSpace(turn.Content))
                    continue;
                var turnRole = string.Equals(
                    turn.Role,
                    "assistant",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? ChatRole.Assistant
                    : ChatRole.User;
                conversation.Add(new ChatMessage(turnRole, turn.Content));
            }
        }
        conversation.Add(new ChatMessage(ChatRole.User, request.Message));

        var sw = Stopwatch.StartNew();
        try
        {
            // M.E.AI's UseFunctionInvocation() (already wired on the
            // Anthropic client) drives the tool-call loop internally. We
            // just pass the registry; the model picks tools, the runtime
            // executes them, the result feeds back into the conversation.
            var response = await resolved.Client.GetResponseAsync(
                conversation,
                new ChatOptions
                {
                    ModelId = resolved.Model,
                    Tools = tools.BuildToolsForContext(capCtx),
                },
                ct
            );
            sw.Stop();

            return TypedResults.Ok(
                new ApiResult<AgentChatResult>(
                    new AgentChatResult(
                        Ok: true,
                        Reply: ExtractFinalReply(response),
                        Provider: resolved.Provider,
                        Model: resolved.Model,
                        LatencyMs: sw.ElapsedMilliseconds,
                        Error: null
                    )
                )
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return TypedResults.Ok(
                new ApiResult<AgentChatResult>(
                    new AgentChatResult(
                        Ok: false,
                        Reply: string.Empty,
                        Provider: resolved.Provider,
                        Model: resolved.Model,
                        LatencyMs: sw.ElapsedMilliseconds,
                        Error: ex.Message
                    )
                )
            );
        }
    }

    /// <summary>
    /// Pull the final assistant text out of an M.E.AI <see cref="ChatResponse"/>.
    /// The tool-invocation loop produces multiple assistant messages — one
    /// per LLM turn — and <c>response.Text</c> concatenates ALL of them,
    /// which leaks the model's intermediate "let me think" turns into the
    /// user-visible reply. We want just the last assistant turn (the one
    /// that follows the final tool result).
    ///
    /// Also strips known chat-template control tokens that some open
    /// models (Gemma, Llama variants) leak through OpenAI-compat layers.
    /// </summary>
    private static string ExtractFinalReply(ChatResponse response)
    {
        var lastAssistant = response
            .Messages.Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .LastOrDefault();
        var text = lastAssistant ?? response.Text ?? string.Empty;
        return SanitizeReply(text);
    }

    /// <summary>
    /// Strip chat-template control tokens that occasionally bleed through
    /// from open models. Conservative regex — only patterns we've seen.
    /// </summary>
    private static string SanitizeReply(string text)
    {
        // Known leaks: [END_TOOL_REQUEST], [BEGIN_TOOL_REQUEST],
        // [START_TOOL_REQUEST], [END_OF_TURN], [/INST], <|...|> sentinels.
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[(?:END_TOOL_REQUEST|BEGIN_TOOL_REQUEST|START_TOOL_REQUEST|END_OF_TURN|/?INST)\]",
            string.Empty
        );
        stripped = System.Text.RegularExpressions.Regex.Replace(
            stripped,
            @"<\|[^|]*\|>",
            string.Empty
        );
        return stripped.Trim();
    }

    /// <summary>
    /// System prompt for the in-app assistant. Intentionally short — keep
    /// the context budget for the actual conversation. Carries only what
    /// the assistant needs to know about *who* is asking and *where* they
    /// are; tool descriptions cover *what* the assistant can do.
    /// </summary>
    private static string BuildSystemPrompt(CapabilityContext ctx)
    {
        var screen = string.IsNullOrWhiteSpace(ctx.CurrentScreen) ? "(unknown)" : ctx.CurrentScreen;
        return $$"""
            You are the in-app assistant for Creuser — an open-source workflow + agent
            orchestration platform. Help the operator find features, navigate the UI,
            and understand what the platform can do.

            User role: {{ctx.Role}}
            Current screen: {{screen}}

            Tool guidance:
            - Use `navigate(intent)` for "where do I X" / "how do I X" questions. It
              returns a route and an `expandSection` hint. Render the result as a
              markdown link the user can click — e.g. `[Anthropic settings](/settings/environment?expand=aiAnthropic)`.
            - Use `describe_capabilities(topic?)` when the user is browsing for
              features or asking what they can do. Summarize; don't list everything.
            - If a capability requires Admin and the user is not an admin, tell them
              to ask an admin instead of producing a link they can't use.

            Be concise. Keep replies under three short paragraphs unless the user
            asks for detail. Never make up routes, capability ids, or actions —
            ground every navigation suggestion in a tool result.
            """;
    }
}
