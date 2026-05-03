using System.Text.Json;
using Creuser.Agents;
using Creuser.Core.Execution;
using Creuser.Scripting.ToolLoop;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

/// <summary>
/// Exercises <see cref="LlmToolLoopStepRunner"/> with a scripted
/// <see cref="StubChatClient"/>. The stub's queue lets us assert that the
/// runner advances turn-by-turn, executes tool calls, and rolls
/// transcripts + token counts up correctly.
/// </summary>
public class LlmToolLoopStepRunnerTests : IAsyncLifetime
{
    private string _workingTree = null!;

    public Task InitializeAsync()
    {
        _workingTree = Path.Combine(Path.GetTempPath(), $"creuser-tlr-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingTree);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_workingTree, recursive: true);
        }
        catch
        {
            // best effort
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SingleTurn_NoToolCalls_SucceedsModelDone()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildAssistantText(
                    "Done. Final answer: there are zero matches.",
                    inputTokens: 100,
                    outputTokens: 20
                ),
            }
        );
        var runner = BuildRunner(stub);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["goal"] = "Find all TODO markers in the codebase.",
                ["tools"] = new List<object?> { "read_file", "grep" },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal("model_done", result.Outputs["termination_reason"]);
        Assert.Equal(0, (int)result.Outputs["turns"]!);
        Assert.Equal(0L, (long)result.Outputs["tool_calls"]!);
        Assert.Contains("zero matches", (string)result.Outputs["final_text"]!);
        Assert.Equal(120L, result.TokensUsed);
        Assert.Equal(2, result.Artifacts.Count);
    }

    [Fact]
    public async Task TwoTurn_ModelCallsTool_ThenReturnsFinalAnswer()
    {
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "marker.txt"), "the answer is 42");

        var stub = new StubChatClient(
            new[]
            {
                BuildToolCall(
                    callId: "call_1",
                    name: "read_file",
                    arguments: new Dictionary<string, object?> { ["path"] = "marker.txt" },
                    inputTokens: 80,
                    outputTokens: 10
                ),
                BuildAssistantText(
                    "The file says: the answer is 42.",
                    inputTokens: 100,
                    outputTokens: 15
                ),
            }
        );
        var runner = BuildRunner(stub);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["goal"] = "What's in marker.txt?",
                ["tools"] = new List<object?> { "read_file" },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal("model_done", result.Outputs["termination_reason"]);
        Assert.Equal(1, (int)result.Outputs["turns"]!);
        Assert.Equal(1L, (long)result.Outputs["tool_calls"]!);
        Assert.Contains("answer is 42", (string)result.Outputs["final_text"]!);
        Assert.Equal(205L, result.TokensUsed);

        var toolLogArtifact = result.Artifacts.Single(a => a.Kind == "tool_log");
        var toolLog = JsonSerializer.Deserialize<JsonElement>(toolLogArtifact.Content);
        Assert.Equal(1, toolLog.GetArrayLength());
        Assert.Equal("read_file", toolLog[0].GetProperty("Tool").GetString());
    }

    [Fact]
    public async Task MaxSteps_Exhausted_FailsWithReason()
    {
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "loop.txt"), "x");
        var responses = Enumerable
            .Range(0, 20)
            .Select(i =>
                BuildToolCall(
                    callId: $"call_{i}",
                    name: "read_file",
                    arguments: new Dictionary<string, object?> { ["path"] = "loop.txt" },
                    inputTokens: 1,
                    outputTokens: 1
                )
            );
        var stub = new StubChatClient(responses);
        var runner = BuildRunner(stub);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["goal"] = "Loop forever.",
                ["tools"] = new List<object?> { "read_file" },
                ["max_steps"] = 3,
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal("max_steps", result.Outputs["termination_reason"]);
        Assert.Contains("max_steps", result.ErrorMessage);
        // Transcript should be present despite the failure.
        Assert.Contains(result.Artifacts, a => a.Kind == "transcript");
    }

    [Fact]
    public async Task MaxTokens_Exhausted_FailsWithReason()
    {
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "hot.txt"), "abc");
        var stub = new StubChatClient(
            new[]
            {
                BuildToolCall(
                    callId: "call_1",
                    name: "read_file",
                    arguments: new Dictionary<string, object?> { ["path"] = "hot.txt" },
                    inputTokens: 30_000,
                    outputTokens: 30_000
                ),
                BuildAssistantText("should not get here", inputTokens: 100, outputTokens: 10),
            }
        );
        var runner = BuildRunner(stub);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["goal"] = "Use a lot of tokens.",
                ["tools"] = new List<object?> { "read_file" },
                ["max_tokens"] = 50_000,
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal("max_tokens", result.Outputs["termination_reason"]);
        Assert.Contains("max_tokens", result.ErrorMessage);
    }

    [Fact]
    public async Task UnrecoverableToolError_ShortCircuits()
    {
        var stub = new StubChatClient(
            new[]
            {
                BuildToolCall(
                    callId: "call_1",
                    name: "read_file",
                    arguments: new Dictionary<string, object?> { ["path"] = "../../etc/passwd" },
                    inputTokens: 80,
                    outputTokens: 10
                ),
                BuildAssistantText("should not get here", inputTokens: 50, outputTokens: 5),
            }
        );
        var runner = BuildRunner(stub);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["goal"] = "Try to escape.",
                ["tools"] = new List<object?> { "read_file" },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal("tool_error_unrecoverable", result.Outputs["termination_reason"]);
        Assert.Single(stub.Calls); // one model call, then aborted
    }

    [Fact]
    public async Task UnknownToolName_FailsBeforeAnyCall()
    {
        var stub = new StubChatClient(Array.Empty<ChatResponse>());
        var runner = BuildRunner(stub);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["goal"] = "Anything.",
                ["tools"] = new List<object?> { "made_up_tool" },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("Unknown tool", result.ErrorMessage);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task EmptyGoal_FailsImmediately()
    {
        var stub = new StubChatClient(Array.Empty<ChatResponse>());
        var runner = BuildRunner(stub);
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["goal"] = string.Empty,
                ["tools"] = new List<object?> { "read_file" },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("`goal`", result.ErrorMessage);
    }

    private LlmToolLoopStepRunner BuildRunner(StubChatClient stub)
    {
        var resolver = new StubChatClientResolver(stub);
        var registries = new IToolLoopToolRegistry[] { new WorkspaceToolLoopRegistry() };
        return new LlmToolLoopStepRunner(
            resolver,
            registries,
            TimeProvider.System,
            NullLogger<LlmToolLoopStepRunner>.Instance
        );
    }

    private StepContext BuildContext()
    {
        return new StepContext(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "ws",
            WorkingTreePath: _workingTree,
            StepId: Guid.NewGuid(),
            StepName: "loop-test",
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
}
