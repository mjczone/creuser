namespace Creuser.Core.Repositories;

/// <summary>
/// Tracks which plugin contributed which extension-point key — step type
/// for <c>IStepRunner</c>, registry id for <c>IToolLoopToolRegistry</c>.
/// Populated as plugins call the <c>AddPluginStepRunner</c> /
/// <c>AddPluginToolRegistry</c> helpers during their <c>Configure</c>.
///
/// <para>
/// Read at dispatch time: when a step is about to dispatch, the
/// dispatcher looks up the contributing plugin for the step type; if it's
/// plugin-contributed AND the plugin isn't enabled for the workspace, the
/// step fails with a clear "plugin not enabled" error. Built-in step
/// types (<c>llm-chat</c>, <c>shell</c>, etc.) aren't in the contributions
/// map — they always resolve.
/// </para>
/// </summary>
public interface IPluginContributions
{
    /// <summary>True + plugin id when this step type was contributed by a plugin; false for built-in types.</summary>
    bool TryGetStepRunnerPlugin(string stepType, out string pluginId);

    /// <summary>True + plugin id when this tool-registry type was contributed by a plugin; false for built-in registries.</summary>
    bool TryGetToolRegistryPlugin(Type registryType, out string pluginId);

    /// <summary>Record a step-runner contribution. Called by the plugin's <c>AddPluginStepRunner</c> helper.</summary>
    void RecordStepRunner(string stepType, string pluginId);

    /// <summary>Record a tool-registry contribution. Called by the plugin's <c>AddPluginToolRegistry</c> helper.</summary>
    void RecordToolRegistry(Type registryType, string pluginId);
}
