using Creuser.Plugins.Abstractions;
using Creuser.Scripting.ToolLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Examples.GitHubTools;

/// <summary>
/// GitHub agent-tool plugin — illustrates the canonical pattern for a
/// plugin that contributes <c>IToolLoopToolRegistry</c> tools (i.e.
/// expands what an <c>llm-tool-loop</c> step's agent can do):
///
/// <list type="bullet">
///   <item><b>Tool registry contribution</b> via <c>AddPluginToolRegistry</c> so per-workspace enablement gates the tools.</item>
///   <item><b>Per-workspace settings</b> for the GitHub PAT secret name and default repo.</item>
///   <item><b>Ambient credentials</b> — the LLM doesn't see the PAT or pick which secret to use; the registry resolves these from workspace settings before constructing tools.</item>
///   <item><b>HTTP via <c>IHttpClientFactory</c></b> — testable.</item>
/// </list>
///
/// <para>
/// Setup workflow:
/// <list type="number">
///   <item>Drop the published plugin under <c>&lt;dataDir&gt;/plugins/creuser.examples.githubtools/</c>; restart.</item>
///   <item>Admin enables the plugin for the workspace.</item>
///   <item>Operator stores a GitHub PAT: <c>creuser secrets set github-pat 'ghp_...'</c>.</item>
///   <item>Admin sets plugin settings: <c>{ "patSecretName": "github-pat", "defaultRepo": "owner/repo" }</c>.</item>
///   <item>Job authors include <c>read_pr</c>, <c>list_issues</c>, or <c>comment_on_issue</c> in their <c>llm-tool-loop</c> step's <c>tools:</c> allow-list.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GitHubToolsPlugin : IPluginRegistration
{
    public const string PluginId = "creuser.examples.githubtools";

    public PluginManifest Manifest { get; } =
        new(
            Id: PluginId,
            Name: "GitHub Tools Example",
            Version: "0.1.0",
            Author: "MJCZone",
            Description: "Adds read_pr, list_issues, and comment_on_issue tools to the agentic llm-tool-loop runner. Settings: patSecretName (filename of the GitHub PAT secret), defaultRepo (owner/name; per-call args can override).",
            MinimumHostVersion: "0.1.0",
            Provides: new[]
            {
                "ToolRegistry:GitHub",
                "Tool:read_pr",
                "Tool:list_issues",
                "Tool:comment_on_issue",
            },
            DocumentationUrl: "https://github.com/mjczone/creuser/blob/main/docs/plugin-development.md"
        );

    public void Configure(IServiceCollection services, IPluginContext context)
    {
        services.AddHttpClient(
            "github-plugin",
            c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Creuser-GitHub-Plugin/0.1");
                c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                c.BaseAddress = new Uri("https://api.github.com/");
            }
        );
        services.AddPluginToolRegistry<GitHubToolRegistry>(context);
        // Tool registries are scoped, but the host's tool-loop runner
        // resolves IEnumerable<IToolLoopToolRegistry> via DI — register
        // the concrete type as that interface so the runner picks it up.
        services.AddScoped<IToolLoopToolRegistry, GitHubToolRegistry>();
        context.Logger.LogInformation(
            "GitHub Tools plugin registered tool registry from {Dir}",
            context.PluginDirectory
        );
    }
}
