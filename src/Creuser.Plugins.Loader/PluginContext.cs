using Creuser.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Loader;

/// <summary>
/// Concrete <see cref="IPluginContext"/> the loader hands to each
/// plugin's <see cref="IPluginRegistration.Configure"/> call. Carries
/// the plugin id, a logger pre-scoped to it, and the directory the
/// plugin was loaded from (so plugins shipping auxiliary files can read
/// them relative to their own directory).
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    public string PluginId { get; }
    public ILogger Logger { get; }
    public string PluginDirectory { get; }

    public PluginContext(string pluginId, ILogger logger, string pluginDirectory)
    {
        PluginId = pluginId;
        Logger = logger;
        PluginDirectory = pluginDirectory;
    }
}
