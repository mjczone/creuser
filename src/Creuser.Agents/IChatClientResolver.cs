using Microsoft.Extensions.AI;

namespace Creuser.Agents;

/// <summary>
/// Abstraction over "give me an IChatClient for the configured provider."
/// The host (Creuser.Web) implements this against its environment config +
/// secrets store; downstream layers (Creuser.Scripting, future plugins)
/// consume it without taking a host dependency.
///
/// <para>
/// The implementation is the bridge from <em>configuration</em> (which
/// provider to use, which model, which API key file) to <em>infrastructure</em>
/// (an actual <see cref="IChatClient"/>). Step runners that need an LLM
/// inject this and stay layering-clean.
/// </para>
/// </summary>
public interface IChatClientResolver
{
    /// <summary>
    /// Resolve a chat client for the given (or configured-default) provider.
    /// Returns a <see cref="ChatClientResolution"/> with either a non-null
    /// <see cref="ChatClientResolution.Client"/> on success or a
    /// human-readable <see cref="ChatClientResolution.Reason"/> on failure.
    /// Callers surface the reason directly to operators.
    ///
    /// <para>
    /// The returned client has <c>UseFunctionInvocation()</c> applied, so
    /// callers can pass <see cref="Microsoft.Extensions.AI.AIFunction"/>
    /// tools and the SDK drives the call loop automatically. Callers that
    /// need to drive their own ReAct loop (e.g. <c>LlmToolLoopStepRunner</c>)
    /// should use <see cref="ResolveRawAsync"/> instead.
    /// </para>
    /// </summary>
    Task<ChatClientResolution> ResolveAsync(
        string? provider = null,
        string? modelOverride = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Resolve a "raw" chat client — the same provider construction as
    /// <see cref="ResolveAsync"/> but without the
    /// <c>UseFunctionInvocation()</c> middleware. The caller is responsible
    /// for executing tool calls in the response and feeding results back.
    /// Used by step runners that need explicit per-turn budget control,
    /// per-call audit, or unrecoverable-error short-circuiting.
    /// </summary>
    Task<ChatClientResolution> ResolveRawAsync(
        string? provider = null,
        string? modelOverride = null,
        CancellationToken ct = default
    );
}

public sealed record ChatClientResolution(
    IChatClient? Client,
    string? Provider,
    string? Model,
    string? Reason
);
