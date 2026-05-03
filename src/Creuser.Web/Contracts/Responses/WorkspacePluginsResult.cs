namespace Creuser.Web.Contracts.Responses;

/// <summary>
/// Response shape for <c>GET /api/workspaces/{slug}/plugins</c>. Lists every
/// plugin currently loaded by the platform along with whether it's enabled
/// for this particular workspace. The plugin assembly itself is loaded once
/// per Creuser instance from <c>/data/plugins/</c>; per-workspace
/// <see cref="WorkspacePluginInfo.Enabled"/> gates which workspaces see the
/// plugin's job runners / widgets / agent providers / capabilities.
///
/// <para>
/// Until the plugin loader lands, <see cref="Plugins"/> is always empty and
/// <see cref="Note"/> carries an explainer the SPA renders inline. The wire
/// shape is the production shape so the loader can populate it later
/// without changing the contract.
/// </para>
/// </summary>
public sealed record WorkspacePluginsResult(
    IReadOnlyList<WorkspacePluginInfo> Plugins,
    /// <summary>Operator-facing note shown while the loader isn't wired or no plugins are present. Null once plugins exist.</summary>
    string? Note
);

/// <summary>One plugin's metadata + this workspace's enablement state.</summary>
public sealed record WorkspacePluginInfo(
    /// <summary>Stable identifier from the plugin's manifest — e.g. <c>acme.process_map</c>.</summary>
    string PluginId,
    string Name,
    string Version,
    string? Author,
    string? Description,
    /// <summary>Whether this workspace has opted into the plugin's contributions.</summary>
    bool Enabled,
    /// <summary>One of <c>loaded</c>, <c>failed</c>, <c>incompatible</c>. Drives the status chip in the UI.</summary>
    string Status,
    /// <summary>Free-text status message — load error, incompatibility reason, or "OK" on success.</summary>
    string? StatusMessage,
    /// <summary>Extension points the plugin contributes — e.g. <c>JobRunner:python</c>, <c>Widget:RunInspector</c>, <c>AgentProvider:cohere</c>. Used in the UI to show "what this gives the workspace".</summary>
    IReadOnlyList<string> Provides,
    /// <summary>Host-OS tool dependencies declared in the plugin manifest — e.g. <c>python&gt;=3.12</c>. Surfaces unavailable plugins on slim deployments.</summary>
    IReadOnlyList<string> RequiredTools,
    DateTime? LoadedAt
);
