using System.Reflection;
using Creuser.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Loader;

/// <summary>
/// Walks <c>&lt;dataDir&gt;/plugins/</c>, loads each plugin's main assembly
/// into a dedicated <see cref="PluginAssemblyLoadContext"/>, and
/// instantiates the plugin's <see cref="IPluginRegistration"/>
/// implementation. Failures isolate to the offending plugin: a single
/// bad plugin produces a <see cref="DiscoveredPlugin"/> with
/// <see cref="DiscoveredPlugin.Status"/> = "failed" and a captured
/// status message; other plugins continue to load.
/// </summary>
public sealed class PluginDiscovery
{
    private readonly ILogger<PluginDiscovery> _logger;

    public PluginDiscovery(ILogger<PluginDiscovery> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Discover plugins under <paramref name="pluginsRoot"/>. Each
    /// subdirectory is treated as one plugin; the plugin's main assembly
    /// must be named after the directory (e.g. <c>plugins/hello/hello.dll</c>)
    /// or end with <c>.Plugin.dll</c>. Returns one record per
    /// subdirectory regardless of load outcome — failures are reported
    /// not silently dropped.
    /// </summary>
    public IReadOnlyList<DiscoveredPlugin> Discover(string pluginsRoot)
    {
        var results = new List<DiscoveredPlugin>();
        if (!Directory.Exists(pluginsRoot))
        {
            _logger.LogInformation(
                "Plugins root {Root} does not exist; no plugins to discover",
                pluginsRoot
            );
            return results;
        }

        foreach (
            var dir in Directory
                .EnumerateDirectories(pluginsRoot)
                .OrderBy(p => p, StringComparer.Ordinal)
        )
        {
            var dirName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.'))
                continue;

            try
            {
                results.Add(LoadPlugin(dirName, dir));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Dir} failed to load", dirName);
                results.Add(
                    DiscoveredPlugin.Failed(
                        dirName,
                        dirName,
                        "0.0.0",
                        $"Plugin load threw: {ex.GetType().Name}: {ex.Message}",
                        dir
                    )
                );
            }
        }
        return results;
    }

    private DiscoveredPlugin LoadPlugin(string dirName, string dir)
    {
        // Resolve the main assembly. Convention: <dir-name>.dll, or any
        // file matching *.Plugin.dll, or the single .dll if exactly one
        // exists. Don't try to load every .dll — that's how dependency
        // libraries get mistakenly treated as plugins.
        var primaryDll = ResolvePrimaryAssembly(dir, dirName);
        if (primaryDll is null)
        {
            return DiscoveredPlugin.Failed(
                dirName,
                dirName,
                "0.0.0",
                $"No plugin assembly found under '{dirName}/'. Expected '{dirName}.dll' "
                    + "or a file ending in '.Plugin.dll'.",
                dir
            );
        }

        var alc = new PluginAssemblyLoadContext(dirName, primaryDll);
        Assembly assembly;
        try
        {
            assembly = alc.LoadFromAssemblyPath(primaryDll);
        }
        catch (Exception ex)
        {
            return DiscoveredPlugin.Failed(
                dirName,
                dirName,
                "0.0.0",
                $"Failed to load assembly '{Path.GetFileName(primaryDll)}': {ex.Message}",
                dir
            );
        }

        var registrationType = assembly
            .GetTypes()
            .FirstOrDefault(t =>
                t is { IsClass: true, IsAbstract: false, IsPublic: true }
                && typeof(IPluginRegistration).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) is not null
            );
        if (registrationType is null)
        {
            return DiscoveredPlugin.Failed(
                dirName,
                dirName,
                "0.0.0",
                $"Assembly '{Path.GetFileName(primaryDll)}' contains no public class with a "
                    + "parameterless constructor that implements IPluginRegistration.",
                dir
            );
        }

        IPluginRegistration registration;
        try
        {
            registration = (IPluginRegistration)Activator.CreateInstance(registrationType)!;
        }
        catch (Exception ex)
        {
            return DiscoveredPlugin.Failed(
                dirName,
                dirName,
                "0.0.0",
                $"Failed to instantiate '{registrationType.FullName}': {ex.Message}",
                dir
            );
        }

        var manifest = registration.Manifest;
        return new DiscoveredPlugin(
            Manifest: manifest,
            Registration: registration,
            LoadContext: alc,
            Directory: dir,
            Status: "loaded",
            StatusMessage: "OK"
        );
    }

    private static string? ResolvePrimaryAssembly(string dir, string dirName)
    {
        var byName = Path.Combine(dir, dirName + ".dll");
        if (File.Exists(byName))
            return byName;
        var pluginSuffix = Directory
            .EnumerateFiles(dir, "*.Plugin.dll", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (pluginSuffix is not null)
            return pluginSuffix;
        var allDlls = Directory
            .EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
            .ToList();
        return allDlls.Count == 1 ? allDlls[0] : null;
    }
}

/// <summary>
/// One plugin discovery outcome. Stored verbatim in <c>cr.plugins</c>.
/// Status is <c>loaded</c> when the plugin's assembly + registration
/// resolved cleanly; <c>failed</c> when something went wrong (status
/// message describes the cause). Failed plugins still get a row so
/// admins see them in the SPA's plugin status page.
/// </summary>
public sealed record DiscoveredPlugin(
    PluginManifest Manifest,
    IPluginRegistration? Registration,
    PluginAssemblyLoadContext? LoadContext,
    string Directory,
    string Status,
    string? StatusMessage
)
{
    public static DiscoveredPlugin Failed(
        string id,
        string name,
        string version,
        string statusMessage,
        string directory
    ) =>
        new(
            Manifest: new PluginManifest(id, name, version),
            Registration: null,
            LoadContext: null,
            Directory: directory,
            Status: "failed",
            StatusMessage: statusMessage
        );
}
