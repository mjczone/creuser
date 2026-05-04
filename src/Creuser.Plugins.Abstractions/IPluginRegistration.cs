using Microsoft.Extensions.DependencyInjection;

namespace Creuser.Plugins.Abstractions;

/// <summary>
/// Contract every Creuser plugin implements. The host's plugin loader
/// discovers plugins by scanning loaded assemblies for non-abstract,
/// public, parameterless-ctor classes that implement this interface,
/// instantiates them, reads their <see cref="Manifest"/>, and invokes
/// <see cref="Configure"/> to let the plugin contribute services to the
/// host's DI container.
///
/// <para>
/// Convention: one <c>IPluginRegistration</c> implementation per plugin.
/// The class name is conventionally <c>&lt;Vendor&gt;Plugin</c>
/// (e.g. <c>HelloPlugin</c>). The host raises a clear error if a plugin
/// assembly contains zero or multiple implementations.
/// </para>
/// </summary>
public interface IPluginRegistration
{
    /// <summary>Plugin identity + metadata. Surfaced on the plugins page.</summary>
    PluginManifest Manifest { get; }

    /// <summary>
    /// Contribute services to the host. The plugin uses standard
    /// <see cref="IServiceCollection"/> extensions to register
    /// <see cref="Creuser.Core.Execution.IStepRunner"/> implementations,
    /// <c>ICapabilityProvider</c> implementations, <c>IToolLoopToolRegistry</c>
    /// implementations, and any plugin-internal services it needs.
    ///
    /// <para>
    /// The <see cref="IPluginContext"/> carries plugin-scoped facilities —
    /// a logger, a settings store keyed on the plugin id, the secrets
    /// service. Plugins should resolve host services via the standard DI
    /// pattern (constructor injection on the registered classes); the
    /// context is for use during the registration call itself when the
    /// plugin needs to read its own settings to drive registration choices.
    /// </para>
    /// </summary>
    void Configure(IServiceCollection services, IPluginContext context);
}
