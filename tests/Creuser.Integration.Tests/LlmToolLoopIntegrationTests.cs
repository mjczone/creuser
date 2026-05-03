using System.Net.Http.Json;
using System.Text.Json;
using Creuser.Agents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end llm-tool-loop test. Replaces the
/// <see cref="IChatClientResolver"/> in the WAF with a deterministic stub
/// that emits a scripted tool-call → final-answer pair, drives the runner
/// through the JobExecutor, and asserts the persisted run shape carries
/// the loop's transcript + tool_log artifacts and the agent's final
/// output.
/// </summary>
public sealed class LlmToolLoopIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;
    private TestChatClientResolver _resolver = null!;

    public LlmToolLoopIntegrationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _resolver = new TestChatClientResolver();
        _factory = new CreuserApiFactory
        {
            ConnectionString = _pg.ConnectionString,
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IChatClientResolver>();
                services.AddSingleton<IChatClientResolver>(_resolver);
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

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-tloop-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(_workspacePath, "audit-target.md"),
            "the canonical answer is 42"
        );

        _workspaceSlug = $"tlp-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Tool Loop Workspace",
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
            Directory.Delete(_workspacePath, recursive: true);
        }
        catch
        {
            // best effort
        }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task SingleStep_LlmToolLoop_RunsLoopAndPersistsArtifacts()
    {
        // Stub conversation: the agent calls read_file once, then emits a
        // final answer that cites the file content.
        _resolver.Enqueue(
            BuildToolCall(
                "call_1",
                "read_file",
                new Dictionary<string, object?> { ["path"] = "audit-target.md" },
                inputTokens: 200,
                outputTokens: 30
            )
        );
        _resolver.Enqueue(
            BuildAssistantText(
                "Based on read_file('audit-target.md'), the answer is 42.",
                inputTokens: 220,
                outputTokens: 25
            )
        );

        var jobId = await CreateLlmToolLoopJob(
            slug: "investigate",
            goal: "Find the canonical answer in audit-target.md.",
            tools: new[] { "read_file" }
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("succeeded", result.GetProperty("status").GetString());

        var runId = result.GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        detail.EnsureSuccessStatusCode();
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        Assert.Equal("llm-tool-loop", step.GetProperty("stepType").GetString());
        Assert.Equal("succeeded", step.GetProperty("status").GetString());

        var outputs = step.GetProperty("outputsJson").GetString();
        Assert.Contains("model_done", outputs);
        Assert.Contains("the answer is 42", outputs);

        // Token total — sum of the two stub responses' input + output.
        Assert.Equal(475L, step.GetProperty("tokensUsed").GetInt64());
    }

    [Fact]
    public async Task MultiStepDag_LlmToolLoopFeedsFileMutate_WritesAgentDerivedFile()
    {
        _resolver.Enqueue(
            BuildToolCall(
                "call_1",
                "read_file",
                new Dictionary<string, object?> { ["path"] = "audit-target.md" },
                inputTokens: 200,
                outputTokens: 30
            )
        );
        // Final answer is JSON-shaped per response_format_json so the
        // downstream file-mutate can pull a field out.
        _resolver.Enqueue(
            BuildAssistantText(
                "{ \"finding\": \"the canonical answer is 42\", \"path\": \"audit-target.md\" }",
                inputTokens: 220,
                outputTokens: 40
            )
        );

        var frontmatter = """
            pattern: agentic
            steps:
              - id: investigate
                type: llm-tool-loop
                inputs:
                  goal: Find the answer in audit-target.md.
                  tools:
                    - read_file
                  response_format_json: |
                    { "type": "object", "properties": { "finding": {"type":"string"}, "path": {"type":"string"} } }
              - id: write
                type: file-mutate
                depends_on:
                  - investigate
                inputs:
                  ops:
                    - op: create
                      path: out/findings.md
                      content: $investigate.final_text
            """;
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "investigate-and-write",
                name = "investigate-and-write",
                description = (string?)null,
                pattern = "agentic",
                frontmatter,
                body = "",
                status = "active",
            }
        );
        resp.EnsureSuccessStatusCode();
        using var jobDoc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var jobId = jobDoc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "succeeded",
            doc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );

        // file-mutate downstream should have written the agent's final_text
        // verbatim to out/findings.md.
        var written = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, "out", "findings.md")
        );
        Assert.Contains("canonical answer is 42", written);
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

    /// <summary>
    /// Test-only resolver returning a single shared stub client. Its
    /// response queue is mutated by tests via <see cref="Enqueue"/> before
    /// triggering the run.
    /// </summary>
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
}
