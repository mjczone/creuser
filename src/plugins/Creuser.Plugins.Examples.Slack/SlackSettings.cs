namespace Creuser.Plugins.Examples.Slack;

/// <summary>
/// Per-workspace settings shape for the Slack plugin. Stored as JSON in
/// <c>cr.workspace_plugin_settings</c> and deserialized at execution
/// time by <see cref="SlackPostStepRunner"/>.
///
/// <para>
/// Secret values themselves DON'T live here — only the FILENAME of a
/// secret in <c>/data/secrets/</c>. The plugin reads the actual value
/// via <c>ISecretsReader</c>. This keeps secret values out of the
/// queryable database.
/// </para>
/// </summary>
public sealed record SlackSettings(
    /// <summary>Filename of a secret in <c>/data/secrets/</c> that holds the Slack webhook URL.</summary>
    string? WebhookSecretName = null,
    /// <summary>Default channel override (Slack webhooks have a default; this overrides per-workspace).</summary>
    string? DefaultChannel = null,
    /// <summary>Default username for posted messages.</summary>
    string? DefaultUsername = null
);
