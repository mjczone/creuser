using Creuser.Core.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Loader;

/// <summary>
/// Hosted service that runs once at host startup to persist the
/// discovered-and-activated plugin set into <c>cr.plugins</c> and
/// populate the in-process <see cref="PluginRegistry"/>. The plugin
/// loader work that happens BEFORE host build (discovery + DI
/// contribution via <see cref="PluginActivator"/>) is the meat; this
/// initializer just commits the outcome to durable + queryable state.
///
/// <para>
/// Each startup the table is fully refreshed: any plugin previously in
/// the table that no longer exists on disk goes away (UI no longer
/// shows it), and per-workspace enablement rows in
/// <c>cr.workspace_plugins</c> are kept (they reference plugin ids,
/// so a re-added plugin picks up its old enablement).
/// </para>
/// </summary>
public sealed class PluginInitializer : IHostedService
{
    private readonly PluginRegistry _registry;
    private readonly IPluginRecordStore _store;
    private readonly IReadOnlyList<DiscoveredPlugin> _discovered;
    private readonly TimeProvider _time;
    private readonly ILogger<PluginInitializer> _logger;

    public PluginInitializer(
        PluginRegistry registry,
        IPluginRecordStore store,
        IReadOnlyList<DiscoveredPlugin> discovered,
        TimeProvider time,
        ILogger<PluginInitializer> logger
    )
    {
        _registry = registry;
        _store = store;
        _discovered = discovered;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsBuildTimeOpenApiGeneration())
            return;

        var loadedAt = _time.GetUtcNow().UtcDateTime;
        var registered = _discovered
            .Select(p => new RegisteredPlugin(
                Manifest: PluginRegistry.Snapshot(p.Manifest),
                Status: p.Status,
                StatusMessage: p.StatusMessage,
                LoadedAt: loadedAt
            ))
            .ToList();
        _registry.Initialize(registered);

        try
        {
            await _store.ReplaceAllAsync(registered, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist plugin registry to cr.plugins");
        }

        if (registered.Count > 0)
            _logger.LogInformation(
                "Plugin loader: {LoadedCount} loaded, {FailedCount} failed",
                registered.Count(p => p.Status == "loaded"),
                registered.Count(p => p.Status == "failed")
            );
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsBuildTimeOpenApiGeneration()
    {
        var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return (
                entryName is not null
                && entryName.Contains("getdocument", StringComparison.OrdinalIgnoreCase)
            )
            || Environment.CommandLine.Contains(
                "dotnet-getdocument",
                StringComparison.OrdinalIgnoreCase
            );
    }
}

/// <summary>
/// Persistence seam for the plugin registry. Concrete implementation
/// (Dapper against <c>cr.plugins</c>) lives in Creuser.Persistence.
/// </summary>
public interface IPluginRecordStore
{
    Task ReplaceAllAsync(IReadOnlyList<RegisteredPlugin> plugins, CancellationToken ct = default);

    Task<IReadOnlyList<RegisteredPlugin>> ListAsync(CancellationToken ct = default);
}
