namespace Creuser.Plugins.Examples.GitHubTools;

/// <summary>
/// Per-workspace settings for the GitHub Tools plugin. Stored as JSON
/// in <c>cr.workspace_plugin_settings</c>, deserialized by
/// <see cref="GitHubToolRegistry"/> at the start of each
/// <c>llm-tool-loop</c> step so the LLM never sees credentials.
/// </summary>
public sealed record GitHubSettings(
    /// <summary>Filename of the secret holding the GitHub Personal Access Token (PAT).</summary>
    string? PatSecretName = null,
    /// <summary>Default <c>owner/name</c> used when tool args don't specify a repo.</summary>
    string? DefaultRepo = null,
    /// <summary>Optional GitHub Enterprise base URL. Null uses public GitHub.</summary>
    string? BaseUrl = null
);
