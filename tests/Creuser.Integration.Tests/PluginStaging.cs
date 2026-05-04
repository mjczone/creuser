namespace Creuser.Integration.Tests;

/// <summary>
/// Helpers for staging an example plugin's published DLL into a test's
/// per-test data directory so the host's plugin loader can pick it up
/// at startup. The plugin's framework + host references aren't bundled
/// (the loader's per-plugin <c>AssemblyLoadContext</c> falls back to the
/// host's already-loaded references for those), so only the plugin's
/// own assembly needs to be copied.
/// </summary>
internal static class PluginStaging
{
    /// <summary>
    /// Copy <c><paramref name="assemblyName"/>.dll</c> from the example
    /// plugin's build output into <paramref name="targetDir"/>. Caller
    /// passes the directory the plugin loader should treat as the
    /// plugin's root (e.g. <c>&lt;dataDir&gt;/plugins/&lt;plugin-id&gt;/</c>).
    /// </summary>
    public static void StagePluginDll(
        string assemblyName,
        string pluginIdDirectory,
        string targetDir
    )
    {
        var solutionRoot = FindSolutionRoot();
        var pluginProjectDir = Path.Combine(solutionRoot, "src", "plugins", assemblyName);
        var candidates = new[]
        {
            Path.Combine(solutionRoot, ".data", "plugins", pluginIdDirectory),
            Path.Combine(pluginProjectDir, "bin", "Debug", "net10.0", "publish"),
            Path.Combine(pluginProjectDir, "bin", "Debug", "net10.0"),
            Path.Combine(pluginProjectDir, "bin", "Release", "net10.0", "publish"),
            Path.Combine(pluginProjectDir, "bin", "Release", "net10.0"),
        };
        var dllName = assemblyName + ".dll";
        var source = candidates.FirstOrDefault(p =>
            Directory.Exists(p) && File.Exists(Path.Combine(p, dllName))
        );
        if (source is null)
            throw new InvalidOperationException(
                $"{assemblyName} build output not found. Build the project before running this test."
            );
        File.Copy(Path.Combine(source, dllName), Path.Combine(targetDir, dllName), true);
    }

    public static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Creuser.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not find solution root from " + AppContext.BaseDirectory
        );
    }
}
