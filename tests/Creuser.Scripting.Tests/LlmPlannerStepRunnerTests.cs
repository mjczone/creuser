using System.Text.Json;
using Creuser.Agents;
using Creuser.Core.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

/// <summary>
/// Unit tests for <see cref="LlmPlannerStepRunner"/> using a scripted
/// <see cref="StubChatClient"/> + an in-memory <see cref="IJobPlanStore"/>.
/// Integration of the planner with <c>JobExecutor</c>'s plan-then-execute
/// path lives in the integration tests.
/// </summary>
public class LlmPlannerStepRunnerTests
{
    [Fact]
    public async Task ValidPlan_PersistsPlanAndReturnsPlanId()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildAssistantText(
                    """
                    {
                      "reasoning": "Trivial plan: check workspace, summarise.",
                      "steps": [
                        { "id": "scan", "type": "projection-sync", "depends_on": [], "inputs": {} },
                        { "id": "summarise", "type": "llm-chat", "depends_on": ["scan"], "inputs": { "prompt": "Summarise the entities." } }
                      ]
                    }
                    """,
                    inputTokens: 200,
                    outputTokens: 60
                ),
            }
        );
        var plans = new InMemoryJobPlanStore();
        var runner = BuildRunner(stub, plans);

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["goal"] = "Investigate workspace" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.IsType<Guid>(result.Outputs["plan_id"]);
        Assert.Equal(2, (int)result.Outputs["step_count"]!);

        var planId = (Guid)result.Outputs["plan_id"]!;
        var saved = plans.Saved[planId];
        Assert.Equal("Investigate workspace", saved.Goal);
        Assert.Contains("Trivial plan", saved.Reasoning);
        Assert.Contains("scan", saved.StepsJson);
        Assert.Contains("summarise", saved.StepsJson);
    }

    [Fact]
    public async Task NoGoal_FailsImmediately()
    {
        var stub = new StubChatClient(Array.Empty<ChatResponse>());
        var runner = BuildRunner(stub, new InMemoryJobPlanStore());
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["goal"] = string.Empty },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("`goal`", result.ErrorMessage);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task UnparseableJson_Fails()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildAssistantText(
                    "Sure, here's the plan: it does some stuff and then more stuff.",
                    inputTokens: 50,
                    outputTokens: 20
                ),
            }
        );
        var runner = BuildRunner(stub, new InMemoryJobPlanStore());
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["goal"] = "Do something" },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("no parseable JSON", result.ErrorMessage);
    }

    [Fact]
    public async Task EmptySteps_Fails()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildAssistantText(
                    """
                    { "reasoning": "Nothing to do.", "steps": [] }
                    """,
                    inputTokens: 30,
                    outputTokens: 10
                ),
            }
        );
        var runner = BuildRunner(stub, new InMemoryJobPlanStore());
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["goal"] = "Do nothing" },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("no `steps`", result.ErrorMessage);
    }

    [Fact]
    public async Task DuplicateStepId_Fails()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildAssistantText(
                    """
                    {
                      "reasoning": "Bad plan.",
                      "steps": [
                        { "id": "dup", "type": "llm-chat", "inputs": { "prompt": "a" } },
                        { "id": "dup", "type": "llm-chat", "inputs": { "prompt": "b" } }
                      ]
                    }
                    """,
                    inputTokens: 50,
                    outputTokens: 30
                ),
            }
        );
        var runner = BuildRunner(stub, new InMemoryJobPlanStore());
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["goal"] = "Bad" },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("duplicated", result.ErrorMessage);
    }

    [Fact]
    public async Task DependsOnUnknownStep_Fails()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildAssistantText(
                    """
                    {
                      "reasoning": "Broken graph.",
                      "steps": [
                        { "id": "a", "type": "llm-chat", "depends_on": ["nonexistent"], "inputs": { "prompt": "x" } }
                      ]
                    }
                    """,
                    inputTokens: 30,
                    outputTokens: 20
                ),
            }
        );
        var runner = BuildRunner(stub, new InMemoryJobPlanStore());
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["goal"] = "Try" },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("not declared in the plan", result.ErrorMessage);
    }

    [Fact]
    public async Task JsonInsideMarkdownFence_StillParses()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildAssistantText(
                    """
                    Here's my plan:
                    ```json
                    {
                      "reasoning": "Single chat.",
                      "steps": [{ "id": "ask", "type": "llm-chat", "inputs": { "prompt": "hi" } }]
                    }
                    ```
                    Hope that works.
                    """,
                    inputTokens: 40,
                    outputTokens: 30
                ),
            }
        );
        var plans = new InMemoryJobPlanStore();
        var runner = BuildRunner(stub, plans);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["goal"] = "Be friendly" },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(1, (int)result.Outputs["step_count"]!);
    }

    private static LlmPlannerStepRunner BuildRunner(StubChatClient stub, InMemoryJobPlanStore plans)
    {
        var resolver = new StubChatClientResolver(stub);
        return new LlmPlannerStepRunner(
            resolver,
            plans,
            TimeProvider.System,
            NullLogger<LlmPlannerStepRunner>.Instance
        );
    }

    private static StepContext BuildContext()
    {
        var workingTree = Path.Combine(Path.GetTempPath(), $"creuser-planner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingTree);
        return new StepContext(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "ws",
            WorkingTreePath: workingTree,
            StepId: Guid.NewGuid(),
            StepName: "planner-test",
            Budgets: new StepBudgets(),
            Logger: NullLogger.Instance,
            AllowedCommands: null,
            RequiredSecrets: null,
            ResumeToken: null
        );
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

    private sealed class StubChatClientResolver : IChatClientResolver
    {
        private readonly StubChatClient _client;

        public StubChatClientResolver(StubChatClient client)
        {
            _client = client;
        }

        public Task<ChatClientResolution> ResolveAsync(
            string? provider = null,
            string? modelOverride = null,
            CancellationToken ct = default
        ) =>
            Task.FromResult(
                new ChatClientResolution(
                    Client: _client,
                    Provider: "stub",
                    Model: "stub-model",
                    Reason: null
                )
            );

        public Task<ChatClientResolution> ResolveRawAsync(
            string? provider = null,
            string? modelOverride = null,
            CancellationToken ct = default
        ) => ResolveAsync(provider, modelOverride, ct);
    }

    private sealed class InMemoryJobPlanStore : IJobPlanStore
    {
        public Dictionary<Guid, JobPlan> Saved { get; } = new();

        public Task<JobPlan?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<JobPlan?>(Saved.TryGetValue(id, out var p) ? p : null);

        public Task SaveAsync(JobPlan plan, CancellationToken ct = default)
        {
            Saved[plan.Id] = plan;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JobPlan>> ListByWorkspaceAsync(
            Guid workspaceId,
            int skip,
            int take,
            CancellationToken ct = default
        ) =>
            Task.FromResult<IReadOnlyList<JobPlan>>(
                Saved.Values.Where(p => p.WorkspaceId == workspaceId).Skip(skip).Take(take).ToList()
            );

        public Task<IReadOnlyList<JobPlan>> ListByScriptAsync(
            Guid jobScriptId,
            int skip,
            int take,
            CancellationToken ct = default
        ) =>
            Task.FromResult<IReadOnlyList<JobPlan>>(
                Saved.Values.Where(p => p.JobScriptId == jobScriptId).Skip(skip).Take(take).ToList()
            );
    }
}
