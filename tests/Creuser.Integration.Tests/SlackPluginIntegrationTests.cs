using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end test for the Slack example plugin. Stages the Slack plugin
/// DLL into a temp data dir, overrides the named <c>slack-plugin</c>
/// HttpClient with a stub message handler, exercises the
/// <c>slack-post</c> step type via the jobs API, and asserts the stub
/// captured the webhook call (URL, payload). Also verifies the
/// per-workspace enablement gate fires when the plugin is disabled.
/// </summary>
public sealed class SlackPluginIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dataDir = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;
    private CapturingHandler _slackHandler = null!;

    public SlackPluginIntegrationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"creuser-slack-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var pluginDir = Path.Combine(_dataDir, "plugins", "creuser.examples.slack");
        Directory.CreateDirectory(pluginDir);
        PluginStaging.StagePluginDll(
            "Creuser.Plugins.Examples.Slack",
            "creuser.examples.slack",
            pluginDir
        );

        _slackHandler = new CapturingHandler();
        _factory = new CreuserApiFactory
        {
            ConnectionString = _pg.ConnectionString,
            DataDir = _dataDir,
            ConfigureTestServices = services =>
            {
                // The Slack plugin's Configure() registered a named
                // "slack-plugin" HttpClient with the production primary
                // handler. Re-registering the named client and pinning its
                // primary handler to our capturing stub overrides the
                // outbound transport without changing plugin code.
                services
                    .AddHttpClient("slack-plugin")
                    .ConfigurePrimaryHttpMessageHandler(() => _slackHandler);
            },
        };
        _client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await Login("admin@creuser.test", "ChangeMe!");

        _workspacePath = Path.Combine(_dataDir, "workspace");
        Directory.CreateDirectory(_workspacePath);
        _workspaceSlug = $"slk-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Slack Plugin Test",
                description = "fixture",
                type = "local",
                localSettings = new { path = _workspacePath, writable = true },
            }
        );
        createWs.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // best effort
        }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task SlackPost_WhenEnabledAndConfigured_PostsToWebhook()
    {
        // Enable the plugin for this workspace.
        var enableResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.slack",
            new { enabled = true }
        );
        enableResp.EnsureSuccessStatusCode();

        // Save the webhook URL as a secret.
        var secretsDir = Path.Combine(_dataDir, "secrets");
        Directory.CreateDirectory(secretsDir);
        await File.WriteAllTextAsync(
            Path.Combine(secretsDir, "slack-test.url"),
            "https://hooks.slack.com/services/TEST/TEST/test-token"
        );

        // Save plugin settings via the new settings endpoint.
        var setSettings = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.slack/settings",
            new
            {
                settings = new
                {
                    webhookSecretName = "slack-test.url",
                    defaultChannel = "#alerts",
                    defaultUsername = "creuser-bot",
                },
            }
        );
        setSettings.EnsureSuccessStatusCode();

        // Pre-arm the stub to return 200 OK with Slack's typical response body.
        _slackHandler.Respond = (_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok", Encoding.UTF8, "text/plain"),
                }
            );

        // Create + run a slack-post job.
        var jobResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "say-hi",
                name = "say hi to slack",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: slack-post\ninputs:\n  text: \"hello from creuser\"\n  icon_emoji: \":robot_face:\"\n",
                body = string.Empty,
                status = "active",
            }
        );
        jobResp.EnsureSuccessStatusCode();
        using var jobDoc = await JsonDocument.ParseAsync(await jobResp.Content.ReadAsStreamAsync());
        var jobId = jobDoc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var runDoc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "succeeded",
            runDoc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );

        // Assert the stub captured the outbound webhook call.
        Assert.Single(_slackHandler.Captured);
        var req = _slackHandler.Captured[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://hooks.slack.com/services/TEST/TEST/test-token", req.Url.ToString());
        using var payloadDoc = JsonDocument.Parse(req.Body);
        Assert.Equal("hello from creuser", payloadDoc.RootElement.GetProperty("text").GetString());
        Assert.Equal("#alerts", payloadDoc.RootElement.GetProperty("channel").GetString());
        Assert.Equal("creuser-bot", payloadDoc.RootElement.GetProperty("username").GetString());
        Assert.Equal(":robot_face:", payloadDoc.RootElement.GetProperty("icon_emoji").GetString());

        // Step outputs reflect the post.
        var runId = runDoc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        Assert.Equal("slack-post", step.GetProperty("stepType").GetString());
        Assert.Equal("succeeded", step.GetProperty("status").GetString());
        var outputs = JsonDocument.Parse(step.GetProperty("outputsJson").GetString()!);
        Assert.True(outputs.RootElement.GetProperty("posted").GetBoolean());
        Assert.Equal(200, outputs.RootElement.GetProperty("http_status").GetInt32());
    }

    [Fact]
    public async Task SlackPost_WhenPluginDisabled_FailsWithEnablementGateError()
    {
        // Do NOT enable the plugin. The dispatch handler's gate should
        // reject the step with a clear message before any HTTP traffic.
        var jobResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "blocked-by-gate",
                name = "should not post",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: slack-post\ninputs:\n  text: \"nope\"\n",
                body = string.Empty,
                status = "active",
            }
        );
        jobResp.EnsureSuccessStatusCode();
        using var jobDoc = await JsonDocument.ParseAsync(await jobResp.Content.ReadAsStreamAsync());
        var jobId = jobDoc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var runDoc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "failed",
            runDoc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );

        var runId = runDoc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        Assert.Equal("slack-post", step.GetProperty("stepType").GetString());
        Assert.Equal("failed", step.GetProperty("status").GetString());
        var error = step.GetProperty("errorMessage").GetString()!;
        Assert.Contains("creuser.examples.slack", error);
        Assert.Contains("not enabled", error, StringComparison.OrdinalIgnoreCase);

        // No HTTP traffic should have left the host.
        Assert.Empty(_slackHandler.Captured);
    }

    [Fact]
    public async Task SlackPost_WhenSecretMissing_FailsWithDiagnosticError()
    {
        // Enable plugin and set settings, but DON'T create the secret file.
        var enableResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.slack",
            new { enabled = true }
        );
        enableResp.EnsureSuccessStatusCode();
        var setSettings = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.slack/settings",
            new { settings = new { webhookSecretName = "slack-missing.url" } }
        );
        setSettings.EnsureSuccessStatusCode();

        var jobResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "no-secret",
                name = "no secret on disk",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: slack-post\ninputs:\n  text: \"won't post\"\n",
                body = string.Empty,
                status = "active",
            }
        );
        jobResp.EnsureSuccessStatusCode();
        using var jobDoc = await JsonDocument.ParseAsync(await jobResp.Content.ReadAsStreamAsync());
        var jobId = jobDoc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var runDoc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "failed",
            runDoc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );
        var runId = runDoc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        var error = step.GetProperty("errorMessage").GetString()!;
        Assert.Contains("slack-missing.url", error);
        Assert.Empty(_slackHandler.Captured);
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Records every outbound HTTP request and lets the test pre-arm a
    /// scripted response. Captures the request URL, method, and serialized
    /// body so assertions don't have to re-read the HttpRequestMessage
    /// after the runner has already disposed it.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Captured { get; } = new();

        public Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>
        > Respond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string body = string.Empty;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            Captured.Add(
                new CapturedRequest(request.Method, request.RequestUri!, body, request.Headers)
            );
            return await Respond(request, cancellationToken);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Url,
        string Body,
        System.Net.Http.Headers.HttpRequestHeaders Headers
    );
}
