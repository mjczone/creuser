using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Abstractions;

/// <summary>
/// Plugin-scoped facilities passed to <see cref="IPluginRegistration.Configure"/>.
/// The plugin uses these to read its own settings, write structured logs
/// scoped to its plugin id, and (when needed) read secrets the operator
/// configured for it.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin's manifest id — same as <see cref="PluginManifest.Id"/>.</summary>
    string PluginId { get; }

    /// <summary>Logger pre-scoped to the plugin id; messages appear under <c>Plugin:&lt;PluginId&gt;</c>.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Absolute filesystem path the plugin was loaded from
    /// (<c>&lt;dataDir&gt;/plugins/&lt;plugin-id&gt;/</c>). Plugins that ship
    /// auxiliary files (templates, schemas, static assets) read them
    /// relative to this path.
    /// </summary>
    string PluginDirectory { get; }
}
