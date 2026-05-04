using System.Reflection;
using System.Runtime.Loader;

namespace Creuser.Plugins.Loader;

/// <summary>
/// Per-plugin <see cref="AssemblyLoadContext"/>. Each discovered plugin
/// loads into its own ALC so:
/// <list type="bullet">
///   <item>Plugins can ship their own dependency versions without colliding with the host or each other (within reason — host-shared types must come from the host's ALC).</item>
///   <item>A future "unload plugin" path becomes possible (this ALC is collectible).</item>
///   <item>Type identity is per-ALC: <c>typeof(Foo)</c> from plugin A is distinct from <c>typeof(Foo)</c> from plugin B even if they share a name.</item>
/// </list>
///
/// <para>
/// <b>Shared-type rule</b>: types that need to flow back to the host
/// (notably the contracts in <c>Creuser.Plugins.Abstractions</c>,
/// <c>Creuser.Core.Execution.IStepRunner</c>, and friends) MUST be
/// loaded from the host's ALC. The <see cref="Load"/> override below
/// returns <c>null</c> for any assembly the host already has loaded —
/// .NET's resolver then falls through to the default ALC, giving us
/// shared identity for those types automatically.
/// </para>
///
/// <para>
/// v1 keeps the isolation model simple: collectible-but-not-actually-unloaded
/// (the host doesn't expose hot-reload), no per-plugin native-library
/// resolution, and crashes during plugin instantiation are caught at
/// the loader level (the plugin appears in the registry with status
/// <c>failed</c>).
/// </para>
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginId, string pluginAssemblyPath)
        : base(name: $"Plugin:{pluginId}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Defer to the default ALC for any assembly the host already has
        // — that's what gives us shared type identity for the abstractions.
        // Returning null tells the runtime to use the parent (Default) ALC.
        var hostLoaded = Default.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, assemblyName.Name)
        );
        if (hostLoaded is not null)
            return null;

        // Plugin-private dependencies: resolve via the deps.json next to
        // the plugin's main assembly.
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
            return LoadFromAssemblyPath(assemblyPath);

        // Fall through — let the default ALC try (handles framework refs).
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}
