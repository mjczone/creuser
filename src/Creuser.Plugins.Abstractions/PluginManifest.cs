namespace Creuser.Plugins.Abstractions;

/// <summary>
/// Identity + metadata for a plugin. Returned by an
/// <see cref="IPluginRegistration"/> implementation; consumed by the host's
/// plugin loader to populate <c>cr.plugins</c> and surface in the SPA's
/// plugin status page.
///
/// <para>
/// The shape is deliberately small. Plugins declare WHAT they are
/// (id, version, description) and WHAT host capabilities they need
/// (<see cref="MinimumHostVersion"/>); WHAT they contribute (step runners,
/// tool registries, capability providers) is declared by the
/// <see cref="IPluginRegistration.Configure"/> method via the standard
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// extensions.
/// </para>
/// </summary>
public sealed record PluginManifest(
    /// <summary>Stable identifier; convention is <c>vendor.feature</c> e.g. <c>creuser.examples.hello</c>. Matches the folder name under <c>/data/plugins/</c>.</summary>
    string Id,
    /// <summary>Human-readable name shown in the plugins page.</summary>
    string Name,
    /// <summary>Semantic version of the plugin itself — distinct from <see cref="MinimumHostVersion"/>.</summary>
    string Version,
    /// <summary>Author / vendor identifier (organization or individual).</summary>
    string? Author = null,
    /// <summary>Free-text one-paragraph description shown on the plugins page.</summary>
    string? Description = null,
    /// <summary>Minimum Creuser version this plugin requires. Loader rejects on mismatch.</summary>
    string? MinimumHostVersion = null,
    /// <summary>Host-OS tool dependencies (e.g. <c>python&gt;=3.12</c>). Surfaces incompatibility on slim deployments.</summary>
    IReadOnlyList<string>? RequiredTools = null,
    /// <summary>Optional hint to the SPA — e.g. <c>StepRunner:hello-world</c>. Free-form strings; the SPA renders them on the plugin card.</summary>
    IReadOnlyList<string>? Provides = null,
    /// <summary>Optional URL to plugin documentation.</summary>
    string? DocumentationUrl = null
);
