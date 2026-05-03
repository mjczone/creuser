namespace Creuser.Web.Agents.Capabilities;

/// <summary>
/// A source of <see cref="Capability"/> entries. Providers compose:
///
/// <list type="bullet">
///   <item><see cref="CoreCapabilityProvider"/> — a hand-written static list of the platform's built-in capabilities. Edited alongside the endpoints they describe.</item>
///   <item>Future <c>EndpointAttributeProvider</c> — reflects over <c>[AiCapability]</c>-decorated endpoints at startup.</item>
///   <item>Future per-workspace / per-plugin providers — return capabilities scoped to the workspace + plugins active in <see cref="CapabilityContext"/>.</item>
/// </list>
///
/// <see cref="CapabilityRegistry"/> aggregates them and filters the result
/// by the calling user's role + visibility rules before handing the list
/// to the AI tool registry.
/// </summary>
public interface ICapabilityProvider
{
    Task<IEnumerable<Capability>> GetAsync(CapabilityContext ctx, CancellationToken ct = default);
}
