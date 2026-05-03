using System.Net.Http.Json;
using System.Text.Json;
using Creuser.Agents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end plan-then-execute test. Replaces the
/// <see cref="IChatClientResolver"/> in the WAF with a deterministic stub
/// that emits a scripted JobPlan, then asserts the run records the
/// planner step + each plan-emitted step in audit order, the persisted
/// JobPlan is fetchable via <c>/plans/{id}</c>, and downstream steps
/// successfully reference <c>$planner.field</c> bindings.
/// </summary>
public sealed class LlmPlannerIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;
    private TestChatClientResolver _resolver = null!;

    public LlmPlannerIntegrationTests(PostgresFixture pg)
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

        _workspacePath = Path.Combine(
            Path.GetTempPath(),
            $"creuser-planner-int-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_workspacePath);

        _workspaceSlug = $"plr-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Planner Workspace",
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
    public async Task PlanThenExecute_RunsPlannerThenWalksPlanSteps()
    {
        // The planner emits a 2-step plan: file-mutate to create a marker,
        // then file-mutate to create a second marker referencing the
        // planner's reasoning. Two queued responses: 1) the planner's plan
        // (single LLM call). The downstream steps are deterministic so no
        // additional LLM responses needed.
        _resolver.Enqueue(
            BuildAssistantText(
                """
                {
                  "reasoning": "Two markers — first claims the workspace, second cites the first.",
                  "steps": [
                    { "id": "first", "type": "file-mutate", "depends_on": ["planner"], "inputs": { "ops": [{ "op": "create", "path": "first.txt", "content": "claimed" }] } },
                    { "id": "second", "type": "file-mutate", "depends_on": ["first"], "inputs": { "ops": [{ "op": "create", "path": "second.txt", "content": "after first" }] } }
                  ]
                }
                """,
                inputTokens: 500,
                outputTokens: 100
            )
        );

        var jobId = await CreateLlmPlannerJob(
            slug: "plan-and-execute",
            goal: "Create two markers in the workspace."
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        var runId = result.GetProperty("runId").GetGuid();

        // Fetch detail first so we can include step errors in any failure
        // diagnostic instead of staring at a bare "failed".
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        detail.EnsureSuccessStatusCode();
        var rawDetail = await detail.Content.ReadAsStringAsync();
        using var detailDoc = JsonDocument.Parse(rawDetail);
        var run = detailDoc.RootElement.GetProperty("result");
        Assert.True(
            string.Equals(
                result.GetProperty("status").GetString(),
                "succeeded",
                StringComparison.Ordinal
            ),
            $"failureMessage={result.GetProperty("failureMessage")}, raw={rawDetail}"
        );

        // The plan id is exposed on the JobRun. Asserting it's non-empty.
        var runResult = result; // run-trigger response is also a JobRunResult
        Guid planId = Guid.Empty;
        if (
            runResult.TryGetProperty("planId", out var planIdProp)
            && planIdProp.ValueKind != JsonValueKind.Null
        )
            planId = planIdProp.GetGuid();
        Assert.NotEqual(Guid.Empty, planId);

        var steps = run.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(3, steps.Count);
        Assert.Equal("llm-planner", steps[0].GetProperty("stepType").GetString());
        Assert.Equal("planner", steps[0].GetProperty("name").GetString());
        Assert.Equal("file-mutate", steps[1].GetProperty("stepType").GetString());
        Assert.Equal("file-mutate", steps[2].GetProperty("stepType").GetString());

        // Files are committed (it's a local workspace, so written directly).
        Assert.True(File.Exists(Path.Combine(_workspacePath, "first.txt")));
        Assert.True(File.Exists(Path.Combine(_workspacePath, "second.txt")));

        // Persisted plan is fetchable via the API.
        var planResp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/plans/{planId}");
        planResp.EnsureSuccessStatusCode();
        using var planDoc = await JsonDocument.ParseAsync(
            await planResp.Content.ReadAsStreamAsync()
        );
        var plan = planDoc.RootElement.GetProperty("result");
        Assert.Equal("Create two markers in the workspace.", plan.GetProperty("goal").GetString());
        Assert.Contains("Two markers", plan.GetProperty("reasoning").GetString());

        // Plans list endpoint returns the row too.
        var listResp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/plans/");
        listResp.EnsureSuccessStatusCode();
        using var listDoc = await JsonDocument.ParseAsync(
            await listResp.Content.ReadAsStreamAsync()
        );
        Assert.Equal(1, listDoc.RootElement.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task PlannerFails_RunFailsAndNoPlanSteps()
    {
        _resolver.Enqueue(
            BuildAssistantText("I cannot help with that.", inputTokens: 30, outputTokens: 10)
        );

        var jobId = await CreateLlmPlannerJob(
            slug: "planner-fails",
            goal: "Plan something the planner can't represent."
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("failed", result.GetProperty("status").GetString());

        var runId = result.GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var steps = detailDoc
            .RootElement.GetProperty("result")
            .GetProperty("steps")
            .EnumerateArray()
            .ToList();
        // Only the planner step (failed) was persisted; no plan steps walked.
        Assert.Single(steps);
        Assert.Equal("llm-planner", steps[0].GetProperty("stepType").GetString());
        Assert.Equal("failed", steps[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task PlanStepReferencesPlannerOutput_BindingResolves()
    {
        // The plan emits one step that references $planner.reasoning in
        // its inputs. file-mutate writes that reasoning to a file, proving
        // the binding worked end-to-end.
        _resolver.Enqueue(
            BuildAssistantText(
                """
                {
                  "reasoning": "Reasoning verbatim",
                  "steps": [
                    {
                      "id": "echo",
                      "type": "file-mutate",
                      "depends_on": ["planner"],
                      "inputs": { "ops": [{ "op": "create", "path": "reasoning.txt", "content": "$planner.reasoning" }] }
                    }
                  ]
                }
                """,
                inputTokens: 200,
                outputTokens: 80
            )
        );

        var jobId = await CreateLlmPlannerJob(slug: "binding-check", goal: "Echo the reasoning.");

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

        var written = await File.ReadAllTextAsync(Path.Combine(_workspacePath, "reasoning.txt"));
        Assert.Equal("Reasoning verbatim", written);
    }

    private async Task<Guid> CreateLlmPlannerJob(string slug, string goal)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug,
                name = slug,
                description = (string?)null,
                pattern = "plan-then-execute",
                frontmatter = "type: llm-planner\n",
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
        ) => throw new NotSupportedException("Streaming not supported.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
