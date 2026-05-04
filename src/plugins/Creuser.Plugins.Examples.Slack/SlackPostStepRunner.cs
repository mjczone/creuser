using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Core.Secrets;

namespace Creuser.Plugins.Examples.Slack;

/// <summary>
/// <c>type: slack-post</c> step runner. Inputs:
/// <list type="bullet">
///   <item><c>text</c> (string, required) — message body. Supports Slack mrkdwn.</item>
///   <item><c>channel</c> (string, optional) — overrides workspace default.</item>
///   <item><c>username</c> (string, optional) — overrides workspace default.</item>
///   <item><c>icon_emoji</c> (string, optional) — e.g. <c>":robot_face:"</c>.</item>
/// </list>
/// Outputs:
/// <list type="bullet">
///   <item><c>posted</c> (bool) — true on 2xx response.</item>
///   <item><c>http_status</c> (int) — Slack's HTTP status code.</item>
///   <item><c>response_body</c> (string) — body for diagnostics.</item>
/// </list>
///
/// <para>
/// Resolution order for the webhook URL:
/// <list type="number">
///   <item>Workspace plugin settings → <c>WebhookSecretName</c> → read from <c>/data/secrets/&lt;name&gt;</c>.</item>
///   <item>Step input <c>webhook_url_secret</c> → read from <c>/data/secrets/&lt;name&gt;</c> (operators can override per-step).</item>
/// </list>
/// If neither is set, the step fails with a clear configuration error.
/// </para>
/// </summary>
public sealed class SlackPostStepRunner : IStepRunner
{
    public string StepType => "slack-post";

    private static readonly JsonSerializerOptions SettingsJsonOptions = new(
        JsonSerializerDefaults.Web
    );

    private readonly IHttpClientFactory _http;
    private readonly ISecretsReader _secrets;
    private readonly IPluginSettingsStore _settings;

    public SlackPostStepRunner(
        IHttpClientFactory http,
        ISecretsReader secrets,
        IPluginSettingsStore settings
    )
    {
        _http = http;
        _secrets = secrets;
        _settings = settings;
    }

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        var text = GetString(inputs, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            sw.Stop();
            return StepResult.Failure(
                "slack-post requires a `text` input — the message body to send.",
                sw.ElapsedMilliseconds
            );
        }

        // Resolve the webhook URL: workspace settings → step input override.
        var settingsJson = await _settings.GetAsync(ctx.WorkspaceId, SlackPlugin.PluginId, ct);
        var settings = string.IsNullOrWhiteSpace(settingsJson)
            ? new SlackSettings()
            : JsonSerializer.Deserialize<SlackSettings>(settingsJson, SettingsJsonOptions)
                ?? new SlackSettings();

        var secretName = GetString(inputs, "webhook_url_secret") ?? settings.WebhookSecretName;
        if (string.IsNullOrWhiteSpace(secretName))
        {
            sw.Stop();
            return StepResult.Failure(
                "slack-post: no webhook URL configured. Set `webhookSecretName` in the plugin settings "
                    + $"(PUT /api/workspaces/{ctx.WorkspaceSlug}/plugins/creuser.examples.slack/settings) "
                    + "or pass `webhook_url_secret` as a step input.",
                sw.ElapsedMilliseconds
            );
        }

        var webhookUrl = await _secrets.ReadAsync(secretName, ct);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            sw.Stop();
            return StepResult.Failure(
                $"slack-post: secret '{secretName}' is empty or missing. Save the Slack webhook URL "
                    + $"to /data/secrets/{secretName}.",
                sw.ElapsedMilliseconds
            );
        }

        var payload = new Dictionary<string, object?> { ["text"] = text };
        var channel = GetString(inputs, "channel") ?? settings.DefaultChannel;
        if (!string.IsNullOrWhiteSpace(channel))
            payload["channel"] = channel;
        var username = GetString(inputs, "username") ?? settings.DefaultUsername;
        if (!string.IsNullOrWhiteSpace(username))
            payload["username"] = username;
        var iconEmoji = GetString(inputs, "icon_emoji");
        if (!string.IsNullOrWhiteSpace(iconEmoji))
            payload["icon_emoji"] = iconEmoji;

        var client = _http.CreateClient("slack-plugin");
        HttpResponseMessage response;
        string body;
        try
        {
            response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return StepResult.Failure(
                $"slack-post: HTTP request failed: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }

        sw.Stop();
        var status = (int)response.StatusCode;
        var posted = response.IsSuccessStatusCode;
        var outputs = new Dictionary<string, object?>
        {
            ["posted"] = posted,
            ["http_status"] = status,
            ["response_body"] = body,
        };

        if (!posted)
            return new StepResult(
                Status: StepStatus.Failed,
                Outputs: outputs,
                FileChanges: Array.Empty<FileChange>(),
                Artifacts: Array.Empty<StepArtifact>(),
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"slack-post: webhook returned HTTP {status}: {body.Trim()}"
            );

        return StepResult.Success(outputs, sw.ElapsedMilliseconds);
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;
}
