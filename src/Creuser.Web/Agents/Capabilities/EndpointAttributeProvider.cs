using System.Reflection;

namespace Creuser.Web.Agents.Capabilities;

/// <summary>
/// Reflects over an assembly at startup and emits a <see cref="Capability"/>
/// for each <see cref="AiCapabilityAttribute"/> found on a method. Stage 2
/// of the capability registry's three-stage evolution — replaces the
/// hand-written entries in <see cref="CoreCapabilityProvider"/> for any
/// capability that lives next to its endpoint method.
///
/// <para>
/// The scan happens once on construction. The instance is registered as a
/// singleton in DI so the reflection cost (single-digit milliseconds at
/// startup, on a small assembly like ours) doesn't repeat per request.
/// </para>
///
/// <para>
/// Plugin assemblies are *not* scanned by this provider — that's stage 3,
/// when plugins arrive with their own <see cref="ICapabilityProvider"/>
/// registrations. This provider intentionally only covers the host's own
/// assembly so the boundary is clear.
/// </para>
/// </summary>
public sealed class EndpointAttributeProvider : ICapabilityProvider
{
    private readonly IReadOnlyList<Capability> _capabilities;

    public EndpointAttributeProvider(Assembly assembly)
    {
        _capabilities = ScanAssembly(assembly);
    }

    /// <summary>Convenience constructor: scans the assembly that defines this type (i.e. Creuser.Web).</summary>
    public EndpointAttributeProvider()
        : this(typeof(EndpointAttributeProvider).Assembly) { }

    public Task<IEnumerable<Capability>> GetAsync(
        CapabilityContext ctx,
        CancellationToken ct = default
    ) => Task.FromResult<IEnumerable<Capability>>(_capabilities);

    private static IReadOnlyList<Capability> ScanAssembly(Assembly assembly)
    {
        // BindingFlags include nonpublic + static because minimal-api endpoint
        // handlers are typically `private static` methods on a `*Endpoints`
        // class. Public/instance methods carrying the attribute work too.
        const BindingFlags flags =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance;

        var capabilities = new List<Capability>();
        foreach (var type in assembly.GetTypes())
        {
            // Skip compiler-generated types (closures, async state machines)
            // — they can't carry meaningful capability attributes and crowd
            // the iteration.
            if (type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
                continue;

            foreach (var method in type.GetMethods(flags))
            {
                var attrs = method.GetCustomAttributes<AiCapabilityAttribute>(inherit: false);
                foreach (var attr in attrs)
                    capabilities.Add(attr.ToCapability());
            }
        }
        return capabilities;
    }
}
