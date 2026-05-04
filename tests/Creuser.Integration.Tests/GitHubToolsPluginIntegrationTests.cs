using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Creuser.Agents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end test for the GitHubTools example plugin, which contributes
/// an <c>IToolLoopToolRegistry</c> with three tools: <c>read_pr</c>,
/// <c>list_issues</c>, <c>comment_on_issue</c>. Stages the plugin DLL,
/// stubs both the chat client (so the agent emits a scripted tool call)
/// and the named <c>github-plugin</c> HttpClient (so the tool's outbound
/// HTTP is captured), and exercises the full <c>llm-tool-loop</c> path.
/// Also verifies that disabling the plugin filters its tools out of the
/// registry union before the loop sees them.
/// </summary>
public sealed class GitHubToolsPluginIntegrationTests
    : IClassFixture<PostgresFixture>,
        IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dataDir = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;
    private CapturingHandler _githubHandler = null!;
    private TestChatClientResolver _resolver = null!;

    public GitHubToolsPluginIntegrationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"creuser-ghtools-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var pluginDir = Path.Combine(_dataDir, "plugins", "creuser.examples.githubtools");
        Directory.CreateDirectory(pluginDir);
        PluginStaging.StagePluginDll(
            "Creuser.Plugins.Examples.GitHubTools",
            "creuser.examples.githubtools",
            pluginDir
        );

        _githubHandler = new CapturingHandler();
        _resolver = new TestChatClientResolver();
        _factory = new CreuserApiFactory
        {
            ConnectionString = _pg.ConnectionString,
            DataDir = _dataDir,
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IChatClientResolver>();
                services.AddSingleton<IChatClientResolver>(_resolver);
                services
                    .AddHttpClient("github-plugin")
                    .ConfigurePrimaryHttpMessageHandler(() => _githubHandler);
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
        _workspaceSlug = $"gh-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "GitHub Tools Test",
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
    public async Task ReadPr_WhenEnabledAndConfigured_FetchesPrJson()
    {
        // Enable + configure plugin.
        await EnableAndConfigure();

        // Stub the GitHub HTTP response — return a small JSON body so the
        // tool result's `data` carries something the assertion can latch on.
        _githubHandler.Respond = (_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"number\":42,\"title\":\"Fix the thing\",\"state\":\"open\"}",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );

        // Stub conversation: agent calls read_pr once with the PR number,
        // then emits the final answer citing the title.
        _resolver.Enqueue(
            BuildToolCall(
                "call_1",
                "read_pr",
                new Dictionary<string, object?> { ["number"] = 42 },
                inputTokens: 200,
                outputTokens: 30
            )
        );
        _resolver.Enqueue(
            BuildAssistantText(
                "PR #42 is titled \"Fix the thing\" and is open.",
                inputTokens: 250,
                outputTokens: 25
            )
        );

        var jobId = await CreateLlmToolLoopJob(
            slug: "investigate-pr",
            goal: "Read PR #42 and report its title.",
            tools: new[] { "read_pr" }
        );
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

        // The plugin sent exactly one outbound request, hitting the
        // PR endpoint with the configured default repo.
        Assert.Single(_githubHandler.Captured);
        var req = _githubHandler.Captured[0];
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.github.com/repos/mjczone/creuser/pulls/42", req.Url.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
        Assert.Equal("ghp_TEST_PAT_VALUE", req.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GitHubTools_WhenPluginDisabled_AreFilteredFromRegistryUnion()
    {
        // Do NOT enable the plugin — its tools should not appear to the
        // tool-loop runner. Asserting on the failure message lets us
        // confirm the filter ran (the unknown-tool message is the runner's
        // way of saying "you asked for a tool that's not available right
        // now" rather than the saga gate's enablement message that fires
        // for plugin-contributed step types).
        var jobId = await CreateLlmToolLoopJob(
            slug: "blocked-by-filter",
            goal: "Try to read PR #42.",
            tools: new[] { "read_pr" }
        );
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
        Assert.Contains("Unknown tool(s): read_pr", error);
        Assert.Contains("creuser.examples.githubtools", error);

        // No HTTP traffic: the loop never called the tool because it
        // wasn't in the registry union.
        Assert.Empty(_githubHandler.Captured);
    }

    [Fact]
    public async Task CommentOnIssue_PostsBodyAndCapturesAuthHeader()
    {
        await EnableAndConfigure();
        _githubHandler.Respond = (_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        "{\"id\":12345,\"body\":\"thanks for reporting\"}",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
        _resolver.Enqueue(
            BuildToolCall(
                "call_1",
                "comment_on_issue",
                new Dictionary<string, object?>
                {
                    ["number"] = 7,
                    ["body"] = "thanks for reporting",
                },
                inputTokens: 200,
                outputTokens: 30
            )
        );
        _resolver.Enqueue(
            BuildAssistantText("Posted the comment.", inputTokens: 220, outputTokens: 20)
        );

        var jobId = await CreateLlmToolLoopJob(
            slug: "comment-on-issue",
            goal: "Comment on issue 7 thanking the reporter.",
            tools: new[] { "comment_on_issue" }
        );
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

        Assert.Single(_githubHandler.Captured);
        var req = _githubHandler.Captured[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(
            "https://api.github.com/repos/mjczone/creuser/issues/7/comments",
            req.Url.ToString()
        );
        using var bodyDoc = JsonDocument.Parse(req.Body);
        Assert.Equal("thanks for reporting", bodyDoc.RootElement.GetProperty("body").GetString());
    }

    private async Task EnableAndConfigure()
    {
        var enableResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.githubtools",
            new { enabled = true }
        );
        enableResp.EnsureSuccessStatusCode();
        var secretsDir = Path.Combine(_dataDir, "secrets");
        Directory.CreateDirectory(secretsDir);
        await File.WriteAllTextAsync(Path.Combine(secretsDir, "github-pat"), "ghp_TEST_PAT_VALUE");
        var setSettings = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.githubtools/settings",
            new { settings = new { patSecretName = "github-pat", defaultRepo = "mjczone/creuser" } }
        );
        setSettings.EnsureSuccessStatusCode();
    }

    private async Task<Guid> CreateLlmToolLoopJob(
        string slug,
        string goal,
        IEnumerable<string> tools
    )
    {
        var toolsYaml = string.Join("", tools.Select(t => $"  - {t}\n"));
        var frontmatter = "type: llm-tool-loop\ninputs:\n  tools:\n" + toolsYaml;
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug,
                name = slug,
                description = (string?)null,
                pattern = "agentic",
                frontmatter,
                body = goal,
                status = "active",
            }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }

    private static ChatResponse BuildAssistantText(string text, int inputTokens, int outputTokens)
    {
        var msg = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse(msg)
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            },
        };
    }

    private static ChatResponse BuildToolCall(
        string callId,
        string name,
        IDictionary<string, object?> arguments,
        int inputTokens,
        int outputTokens
    )
    {
        var msg = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, arguments)]
        );
        return new ChatResponse(msg)
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            },
        };
    }

    private sealed class TestChatClientResolver : IChatClientResolver
    {
        public Queue<ChatResponse> Responses { get; } = new();
        private QueuedChatClient? _client;

        public void Enqueue(ChatResponse response) => Responses.Enqueue(response);

        public Task<ChatClientResolution> ResolveAsync(
            string? provider = null,
            string? modelOverride = null,
            CancellationToken ct = default
        ) => ResolveRawAsync(provider, modelOverride, ct);

        public Task<ChatClientResolution> ResolveRawAsync(
            string? provider = null,
            string? modelOverride = null,
            CancellationToken ct = default
        )
        {
            _client ??= new QueuedChatClient(Responses);
            return Task.FromResult(
                new ChatClientResolution(
                    Client: _client,
                    Provider: "stub",
                    Model: "stub-model",
                    Reason: null
                )
            );
        }
    }

    private sealed class QueuedChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;

        public QueuedChatClient(Queue<ChatResponse> responses)
        {
            _responses = responses;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException(
                    "QueuedChatClient queue is empty — test forgot to enqueue a response."
                );
            return Task.FromResult(_responses.Dequeue());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Streaming not supported by QueuedChatClient.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

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
