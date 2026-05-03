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
    /// </summary>
    Task<ChatClientResolution> ResolveAsync(
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
