using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Creuser.Plugins.Abstractions;

/// <summary>
/// Registration helpers plugins use inside their
/// <see cref="IPluginRegistration.Configure"/>. Each helper does two
/// things: (1) registers the contribution into the host's DI container
/// (so the host can resolve it), and (2) records the contribution in
/// <see cref="IPluginContributions"/> so the host knows which plugin
/// owns this extension point at dispatch time. The runtime enablement
/// gate uses (2) to fail or filter when a workspace hasn't enabled the
/// contributing plugin.
///
/// <para>
/// Plugins authors should ALWAYS use these helpers rather than calling
/// the underlying <c>AddKeyedScoped</c> / <c>AddScoped</c> directly —
/// otherwise their contributions look like built-in platform services
/// to the host and bypass per-workspace enablement.
/// </para>
/// </summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Register a plugin-contributed step runner. Tagged in the
    /// contributions registry so the host can gate dispatch on
    /// per-workspace plugin enablement.
    /// </summary>
    public static IServiceCollection AddPluginStepRunner<TRunner>(
        this IServiceCollection services,
        string stepType,
        IPluginContext context
    )
        where TRunner : class, IStepRunner
    {
        services.AddKeyedScoped<IStepRunner, TRunner>(stepType);
        var contributions = ResolveContributions(services);
        contributions.RecordStepRunner(stepType, context.PluginId);
        return services;
    }

    /// <summary>
    /// Register a plugin-contributed tool-loop tool registry. Tagged in
    /// the contributions registry so the agentic <c>llm-tool-loop</c>
    /// runner can filter the registry out when the workspace hasn't
    /// enabled this plugin.
    /// </summary>
    public static IServiceCollection AddPluginToolRegistry<TRegistry>(
        this IServiceCollection services,
        IPluginContext context
    )
        where TRegistry : class
    {
        // Tool registry interface lives in Creuser.Scripting; we accept
        // any type here and rely on the host's DI to wire it. The tool-loop
        // runner filters by type identity at request time.
        services.AddScoped<TRegistry>();
        var contributions = ResolveContributions(services);
        contributions.RecordToolRegistry(typeof(TRegistry), context.PluginId);
        return services;
    }

    /// <summary>
    /// Lookup the host's <see cref="IPluginContributions"/> from the
    /// service collection. The host registers it as a singleton before
    /// activating plugins; plugins shouldn't normally need to reach it
    /// directly — the helpers above are the intended surface.
    /// </summary>
    private static IPluginContributions ResolveContributions(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IPluginContributions)
        );
        if (descriptor?.ImplementationInstance is IPluginContributions instance)
            return instance;
        throw new InvalidOperationException(
            "IPluginContributions is not registered. The host must register an "
                + "IPluginContributions singleton before activating plugins."
        );
    }
}
