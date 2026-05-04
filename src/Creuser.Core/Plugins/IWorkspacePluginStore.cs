namespace Creuser.Core.Repositories;

/// <summary>
/// Per-workspace plugin enablement persistence. A plugin loaded by the
/// host is only available to a workspace when it has an enabled row
/// here. Surface kept minimal: list, check, set.
/// </summary>
public interface IWorkspacePluginStore
{
    /// <summary>Map of plugin id → enabled flag for one workspace.</summary>
    Task<IReadOnlyDictionary<string, bool>> ListEnablementAsync(
        Guid workspaceId,
        CancellationToken ct = default
    );

    /// <summary>True when this workspace has explicitly opted into the plugin. False otherwise (including when no row exists yet).</summary>
    Task<bool> IsEnabledAsync(Guid workspaceId, string pluginId, CancellationToken ct = default);

    Task SetEnabledAsync(
        Guid workspaceId,
        string pluginId,
        bool enabled,
        Guid? updatedBy,
        CancellationToken ct = default
    );
}
