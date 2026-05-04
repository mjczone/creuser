namespace Creuser.Core.Repositories;

/// <summary>
/// Per-workspace plugin settings — a JSON blob keyed on
/// <c>(workspace_id, plugin_id)</c>. Plugins use this to store
/// configuration the operator sets once and the plugin's runners /
/// tools read at execution time (e.g. a Slack webhook secret-name, a
/// GitHub PAT secret-name, a default channel, default behavior).
///
/// <para>
/// The plugin defines its own settings shape — the host stores the
/// JSON verbatim. Plugin tools / runners deserialize the JSON to their
/// own typed settings record at read time. This decouples plugin
/// settings from the host's schema; new plugins don't require schema
/// changes.
/// </para>
///
/// <para>
/// Secrets DO NOT live in plugin settings. Plugin settings store the
/// FILENAME of a secret (e.g. <c>slack-prod.url</c>); the secret value
/// itself lives in <c>/data/secrets/</c> and is read via
/// <c>SecretsService</c>. This keeps secret values out of the
/// queryable database entirely.
/// </para>
/// </summary>
public interface IPluginSettingsStore
{
    /// <summary>Read the raw settings JSON for one workspace + plugin pair. Null when no row exists.</summary>
    Task<string?> GetAsync(Guid workspaceId, string pluginId, CancellationToken ct = default);

    /// <summary>Upsert settings JSON. Caller validates the JSON shape per plugin contract.</summary>
    Task SetAsync(
        Guid workspaceId,
        string pluginId,
        string settingsJson,
        Guid? updatedBy,
        CancellationToken ct = default
    );

    /// <summary>Delete the settings row. Plugin contributions revert to defaults on next read.</summary>
    Task DeleteAsync(Guid workspaceId, string pluginId, CancellationToken ct = default);
}
