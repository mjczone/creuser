namespace Creuser.Core.Repositories;

/// <summary>
/// Read-only registry of plugins discovered + activated at host startup.
/// Consumers (the plugins API endpoint, the per-workspace enablement
/// gate, the SPA's plugin status page) introspect through this contract;
/// the actual implementation lives in <c>Creuser.Plugins.Loader</c> so
/// Core has no dependency on the loader infrastructure.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>Every plugin discovered at startup, ordered by id. Includes failed plugins.</summary>
    IReadOnlyList<RegisteredPlugin> All { get; }

    /// <summary>Lookup by plugin id. Null when no plugin with that id was discovered.</summary>
    RegisteredPlugin? Find(string pluginId);
}

/// <summary>
/// One discovered + activated plugin, projected to the cross-project
/// surface. Keeps the Core surface free of the
/// <c>Creuser.Plugins.Abstractions</c> types — consumers get the
/// fields they need without taking a reference on the abstractions
/// package.
/// </summary>
public sealed record RegisteredPlugin(
    PluginManifestSnapshot Manifest,
    /// <summary>One of <c>loaded</c>, <c>failed</c>.</summary>
    string Status,
    string? StatusMessage,
    DateTime LoadedAt
);

/// <summary>
/// Manifest fields the host stores + surfaces. Mirrors
/// <c>Creuser.Plugins.Abstractions.PluginManifest</c> but lives in Core
/// so consumers don't take a dependency on the abstractions package.
/// </summary>
public sealed record PluginManifestSnapshot(
    string Id,
    string Name,
    string Version,
    string? Author = null,
    string? Description = null,
    string? MinimumHostVersion = null,
    IReadOnlyList<string>? RequiredTools = null,
    IReadOnlyList<string>? Provides = null,
    string? DocumentationUrl = null
);
