using Creuser.Core.Repositories;
using Creuser.Plugins.Abstractions;

namespace Creuser.Plugins.Loader;

/// <summary>
/// In-memory implementation of <see cref="IPluginRegistry"/>, populated
/// once at host startup by the <see cref="PluginInitializer"/>. Consumers
/// (the plugins API endpoint, the per-workspace enablement gate at
/// runner-resolution time, the SPA's plugin status page) introspect via
/// the Core <see cref="IPluginRegistry"/> contract without depending on
/// Loader internals.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RegisteredPlugin> _plugins = new(StringComparer.Ordinal);

    public IReadOnlyList<RegisteredPlugin> All
    {
        get
        {
            lock (_lock)
                return _plugins.Values.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).ToList();
        }
    }

    public RegisteredPlugin? Find(string pluginId)
    {
        lock (_lock)
            return _plugins.TryGetValue(pluginId, out var p) ? p : null;
    }

    /// <summary>Set the registry's contents (called once at startup).</summary>
    public void Initialize(IEnumerable<RegisteredPlugin> plugins)
    {
        lock (_lock)
        {
            _plugins.Clear();
            foreach (var p in plugins)
                _plugins[p.Manifest.Id] = p;
        }
    }

    /// <summary>
    /// Maps an abstractions <see cref="PluginManifest"/> (loader-internal)
    /// onto the cross-project <see cref="PluginManifestSnapshot"/>.
    /// </summary>
    public static PluginManifestSnapshot Snapshot(PluginManifest manifest) =>
        new(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Author,
            manifest.Description,
            manifest.MinimumHostVersion,
            manifest.RequiredTools,
            manifest.Provides,
            manifest.DocumentationUrl
        );
}
