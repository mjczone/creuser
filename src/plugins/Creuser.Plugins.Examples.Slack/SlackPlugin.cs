using Creuser.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Examples.Slack;

/// <summary>
/// Slack integration plugin — illustrates the canonical pattern for a
/// step-runner plugin that talks to an external service:
///
/// <list type="bullet">
///   <item><b>Per-workspace settings</b> stored in <c>cr.workspace_plugin_settings</c> (the secret filename pointing at the webhook URL).</item>
///   <item><b>Secret resolution</b> via <c>ISecretsReader</c> — the actual webhook URL value lives in <c>/data/secrets/</c> and never leaves the host process.</item>
///   <item><b>HTTP via <c>IHttpClientFactory</c></b> — testable: integration tests inject a stub <c>HttpMessageHandler</c>.</item>
///   <item><b>Plugin-aware registration</b> via <c>AddPluginStepRunner</c> so per-workspace enablement can gate dispatch.</item>
/// </list>
///
/// <para>
/// To enable for a workspace:
/// <list type="number">
///   <item>Drop the published plugin folder under <c>&lt;dataDir&gt;/plugins/creuser.examples.slack/</c>.</item>
///   <item>Restart Creuser. Plugin appears in the Plugins page.</item>
///   <item>Admin enables it for the workspace.</item>
///   <item>Operator stores the Slack webhook URL as a secret: <c>creuser secrets set slack-mywebhook.url 'https://hooks.slack.com/...'</c> (or via the Environment page).</item>
///   <item>Admin sets plugin settings for the workspace: PUT <c>/api/workspaces/{slug}/plugins/creuser.examples.slack/settings</c> body <c>{ "webhookSecretName": "slack-mywebhook.url" }</c>.</item>
///   <item>Authors write jobs with <c>type: slack-post</c> and inputs <c>{ text: "..." }</c>; the runner reads the webhook URL from the secret.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SlackPlugin : IPluginRegistration
{
    public const string PluginId = "creuser.examples.slack";

    public PluginManifest Manifest { get; } =
        new(
            Id: PluginId,
            Name: "Slack Example",
            Version: "0.1.0",
            Author: "MJCZone",
            Description: "Posts messages to Slack via incoming webhook. Settings: webhookSecretName (filename of secret containing the webhook URL). Demonstrates per-workspace plugin settings + secret resolution + IHttpClientFactory for testability.",
            MinimumHostVersion: "0.1.0",
            Provides: new[] { "StepRunner:slack-post" },
            DocumentationUrl: "https://github.com/mjczone/creuser/blob/main/docs/plugin-development.md"
        );

    public void Configure(IServiceCollection services, IPluginContext context)
    {
        // Named HTTP client — keeps Slack calls on a separate connection
        // pool from the host's other HTTP traffic and lets ops tune
        // timeouts/proxies independently.
        services.AddHttpClient(
            "slack-plugin",
            c =>
            {
                c.Timeout = TimeSpan.FromSeconds(15);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Creuser-Slack-Plugin/0.1");
            }
        );
        services.AddPluginStepRunner<SlackPostStepRunner>("slack-post", context);
        context.Logger.LogInformation(
            "Slack plugin registered slack-post step runner from {Dir}",
            context.PluginDirectory
        );
    }
}
