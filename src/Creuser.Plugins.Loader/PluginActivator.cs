using Creuser.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Loader;

/// <summary>
/// Invokes each successfully-discovered plugin's
/// <see cref="IPluginRegistration.Configure"/> against the host's
/// <see cref="IServiceCollection"/>. Plugins that throw during
/// registration get downgraded to <c>failed</c> status (their
/// contributions don't land in DI; existing successful plugins are
/// unaffected).
///
/// <para>
/// Called BEFORE <c>builder.Build()</c> in the host's startup. After
/// activation, the host has DI-resolvable plugin contributions
/// (additional <c>IStepRunner</c> registrations, additional
/// <c>ICapabilityProvider</c>s, additional <c>IToolLoopToolRegistry</c>
/// implementations); the
/// <see cref="PluginInitializer"/> hosted service runs LATER (after
/// <c>app.Build</c>) to persist the registry to <c>cr.plugins</c>.
/// </para>
/// </summary>
public sealed class PluginActivator
{
    public List<DiscoveredPlugin> ActivateAll(
        IReadOnlyList<DiscoveredPlugin> discovered,
        IServiceCollection services,
        ILoggerFactory loggerFactory
    )
    {
        var output = new List<DiscoveredPlugin>(discovered.Count);
        foreach (var plugin in discovered)
        {
            if (plugin.Registration is null)
            {
                output.Add(plugin);
                continue;
            }

            var pluginLogger = loggerFactory.CreateLogger("Plugin:" + plugin.Manifest.Id);
            var ctx = new PluginContext(plugin.Manifest.Id, pluginLogger, plugin.Directory);
            try
            {
                plugin.Registration.Configure(services, ctx);
                output.Add(plugin);
            }
            catch (Exception ex)
            {
                pluginLogger.LogError(
                    ex,
                    "Plugin {PluginId} threw during Configure",
                    plugin.Manifest.Id
                );
                output.Add(
                    plugin with
                    {
                        Status = "failed",
                        StatusMessage =
                            $"Plugin Configure threw: {ex.GetType().Name}: {ex.Message}",
                    }
                );
            }
        }
        return output;
    }
}
