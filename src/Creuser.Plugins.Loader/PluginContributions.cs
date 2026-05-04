using Creuser.Core.Repositories;

namespace Creuser.Plugins.Loader;

/// <summary>
/// Default in-memory <see cref="IPluginContributions"/> implementation.
/// Populated during plugin <c>Configure</c> calls (via the
/// <c>AddPluginStepRunner</c> / <c>AddPluginToolRegistry</c> helpers in
/// <see cref="PluginServiceCollectionExtensions"/>) and read at dispatch
/// time by the saga's <c>StepDispatchHandler</c> + the
/// <c>LlmToolLoopStepRunner</c> when filtering registries.
/// </summary>
public sealed class PluginContributions : IPluginContributions
{
    private readonly Dictionary<string, string> _stepRunners = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _toolRegistries = new();
    private readonly object _lock = new();

    public bool TryGetStepRunnerPlugin(string stepType, out string pluginId)
    {
        lock (_lock)
        {
            if (_stepRunners.TryGetValue(stepType, out var id))
            {
                pluginId = id;
                return true;
            }
        }
        pluginId = string.Empty;
        return false;
    }

    public bool TryGetToolRegistryPlugin(Type registryType, out string pluginId)
    {
        lock (_lock)
        {
            if (_toolRegistries.TryGetValue(registryType, out var id))
            {
                pluginId = id;
                return true;
            }
        }
        pluginId = string.Empty;
        return false;
    }

    public void RecordStepRunner(string stepType, string pluginId)
    {
        lock (_lock)
            _stepRunners[stepType] = pluginId;
    }

    public void RecordToolRegistry(Type registryType, string pluginId)
    {
        lock (_lock)
            _toolRegistries[registryType] = pluginId;
    }
}
