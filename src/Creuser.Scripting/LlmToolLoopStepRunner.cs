using System.Diagnostics;
using System.Text.Json;
using Creuser.Agents;
using Creuser.Core.Execution;
using Creuser.Scripting.ToolLoop;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// Bounded ReAct loop runner. Drives the model-tool-model-tool cycle
/// against the configured chat client until the model produces a final
/// answer or hits a budget. The agentic seam catalogued in
/// <c>architecture.md</c> "Three execution patterns".
///
/// <para>
/// Why this loop is hand-driven rather than relying on
/// <c>UseFunctionInvocation()</c>: per-turn token accounting that rolls
/// into <see cref="StepResult.TokensUsed"/>, explicit budget enforcement
/// points (<c>max_steps</c>, <c>max_tokens</c>, <c>max_duration_seconds</c>)
/// that abort cleanly, per-call audit recording into <c>tool_log.json</c>,
/// and the option to short-circuit on unrecoverable tool errors (path
/// escapes, etc.). The middleware-driven path can't satisfy any of those
/// without wrapping each call in a custom intercepting client.
/// </para>
///
/// <para>
/// v1 ships read-only investigation only — the runner returns
/// <c>FileChanges: []</c>. File mutations live in downstream
/// <c>file-mutate</c> / <c>file-frontmatter</c> steps that consume this
/// runner's <c>final_json</c> output. Honors <c>architecture.md</c>
/// "File mutation discipline" — the executor remains the only writer.
/// </para>
/// </summary>
public sealed class LlmToolLoopStepRunner : IStepRunner
{
    public string StepType => "llm-tool-loop";

    private readonly IChatClientResolver _resolver;
    private readonly IEnumerable<IToolLoopToolRegistry> _registries;
    private readonly TimeProvider _time;
    private readonly ILogger<LlmToolLoopStepRunner> _logger;

    private const int DefaultMaxSteps = 10;
    private const long DefaultMaxTokens = 50_000;
    private static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromSeconds(120);

    private const string DefaultSystemPrompt =
        "You are a code investigator working inside a workspace. Use the provided tools to "
        + "find evidence and answer the user's goal. Cite file paths in your final answer. "
        + "Stop calling tools and produce a final reply once you have enough evidence.";

    public LlmToolLoopStepRunner(
        IChatClientResolver resolver,
        IEnumerable<IToolLoopToolRegistry> registries,
        TimeProvider time,
        ILogger<LlmToolLoopStepRunner> logger
    )
    {
        _resolver = resolver;
        _registries = registries;
        _time = time;
        _logger = logger;
    }

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        var goal = GetString(inputs, "goal");
        if (string.IsNullOrWhiteSpace(goal))
        {
            sw.Stop();
            return StepResult.Failure(
                "llm-tool-loop step requires a `goal` input. For single-step jobs the body of the script is moved into `goal` automatically; check the executor's binding step.",
                sw.ElapsedMilliseconds
            );
        }

        var requestedTools = ReadStringList(inputs, "tools");
        if (requestedTools.Count == 0)
        {
            sw.Stop();
            return StepResult.Failure(
                "llm-tool-loop step requires a non-empty `tools` allow-list. Declare the tools the agent may call (e.g. `tools: [read_file, grep]`).",
                sw.ElapsedMilliseconds
            );
        }

        var systemPrompt = GetString(inputs, "system_prompt") ?? DefaultSystemPrompt;
        var providerOverride = GetString(inputs, "provider");
        var modelOverride = GetString(inputs, "model");
        var responseFormatJson = GetString(inputs, "response_format_json");

        var maxSteps = GetInt(inputs, "max_steps") ?? DefaultMaxSteps;
        var maxTokens = GetLong(inputs, "max_tokens") ?? ctx.Budgets.MaxTokens ?? DefaultMaxTokens;
        var maxDuration =
            GetDurationSeconds(inputs, "max_duration_seconds")
            ?? ctx.Budgets.MaxDuration
            ?? DefaultMaxDuration;

        // Resolve a non-FunctionInvocation-wrapped client. We drive the loop.
        var resolution = await _resolver.ResolveRawAsync(providerOverride, modelOverride, ct);
        if (resolution.Client is null)
        {
            sw.Stop();
            return StepResult.Failure(
                resolution.Reason ?? "Failed to resolve a chat client.",
                sw.ElapsedMilliseconds
            );
        }
        var provider = resolution.Provider ?? "unknown";
        var model = resolution.Model ?? "unknown";

        // Compose registries. Validate that every requested tool exists in
        // the union; map each tool name to the registry that owns it.
        var registries = _registries.ToList();
        if (registries.Count == 0)
        {
            sw.Stop();
            return StepResult.Failure(
                "No IToolLoopToolRegistry implementations are registered. Confirm WorkspaceToolLoopRegistry is wired in DI.",
                sw.ElapsedMilliseconds
            );
        }

        var ownerByName = new Dictionary<string, IToolLoopToolRegistry>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var name in requestedTools)
        {
            var owner = registries.FirstOrDefault(r => r.AvailableTools.Contains(name));
            if (owner is null)
            {
                unknown.Add(name);
                continue;
            }
            if (ownerByName.ContainsKey(name))
            {
                sw.Stop();
                return StepResult.Failure(
                    $"Tool '{name}' is contributed by multiple registries; resolve the conflict before running.",
                    sw.ElapsedMilliseconds
                );
            }
            ownerByName[name] = owner;
        }
        if (unknown.Count > 0)
        {
            var available = string.Join(
                ", ",
                registries.SelectMany(r => r.AvailableTools).Distinct().OrderBy(s => s)
            );
            sw.Stop();
            return StepResult.Failure(
                $"Unknown tool(s): {string.Join(", ", unknown)}. Available tools: {available}.",
                sw.ElapsedMilliseconds
            );
        }

        // Build the AIFunction set per registry, then concat. Each registry
        // gets only the names it owns so closures stay scoped.
        var sink = new ToolLogSink();
        IReadOnlyList<AIFunction> tools;
        try
        {
            var grouped = ownerByName.GroupBy(kv => kv.Value, kv => kv.Key);
            tools = [.. grouped.SelectMany(g => g.Key.BuildTools([.. g], ctx, sink))];
        }
        catch (ToolLoopException ex)
        {
            sw.Stop();
            return StepResult.Failure(ex.Message, sw.ElapsedMilliseconds);
        }
        var toolByName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(responseFormatJson))
        {
            // Append a JSON-format reminder to the system prompt so the
            // model emits parseable JSON as its final message. Schema-aware
            // ChatResponseFormat hookup is forward-looking — Anthropic and
            // OpenAI handle this differently and v1 keeps it simple.
            systemPrompt +=
                "\n\nWhen you have enough evidence, your final reply must be a single JSON value "
                + "conforming to this schema:\n```json\n"
                + responseFormatJson
                + "\n```\nReturn the JSON alone (no prose around it).";
        }

        var conversation = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, goal),
        };

        using var deadlineCts = new CancellationTokenSource(maxDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);

        long tokensUsed = 0;
        long toolCalls = 0;
        var turn = 0;
        var terminationReason = "max_steps";
        string? finalText = null;
        ChatResponse? lastResponse = null;

        while (turn < maxSteps)
        {
            sink.CurrentTurn = turn;

            var options = new ChatOptions { ModelId = model, Tools = [.. tools.Cast<AITool>()] };

            ChatResponse response;
            try
            {
                response = await resolution.Client.GetResponseAsync(
                    conversation,
                    options,
                    linked.Token
                );
            }
            catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
            {
                sw.Stop();
                return BuildPartialFailure(
                    "max_duration",
                    "Loop terminated: max_duration_seconds budget exhausted.",
                    sw.ElapsedMilliseconds,
                    conversation,
                    sink,
                    turn,
                    toolCalls,
                    tokensUsed,
                    model,
                    provider
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                sw.Stop();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "llm-tool-loop call failed on turn {Turn} for step {StepName}",
                    turn,
                    ctx.StepName
                );
                sw.Stop();
                return BuildPartialFailure(
                    "provider_error",
                    $"LLM call failed on turn {turn}: {ex.Message}",
                    sw.ElapsedMilliseconds,
                    conversation,
                    sink,
                    turn,
                    toolCalls,
                    tokensUsed,
                    model,
                    provider
                );
            }

            lastResponse = response;
            tokensUsed += ExtractTokenCount(response) ?? 0;
            foreach (var msg in response.Messages)
                conversation.Add(msg);

            var calls = ExtractFunctionCalls(response);
            if (calls.Count == 0)
            {
                terminationReason = "model_done";
                finalText = ExtractFinalAssistantText(response);
                break;
            }

            // Execute each call sequentially. Most providers emit one
            // call per turn; OpenAI / Anthropic both support parallel calls
            // but executing serially keeps the audit log deterministic.
            foreach (var call in calls)
            {
                toolCalls++;
                if (!toolByName.TryGetValue(call.Name, out var fn))
                {
                    var msg = $"Tool '{call.Name}' is not in the configured allow-list.";
                    conversation.Add(
                        new ChatMessage(
                            ChatRole.Tool,
                            [
                                new FunctionResultContent(
                                    call.CallId,
                                    new { ok = false, error = msg }
                                ),
                            ]
                        )
                    );
                    continue;
                }

                object? result;
                try
                {
                    result = await fn.InvokeAsync(
                        new AIFunctionArguments(call.Arguments),
                        linked.Token
                    );
                }
                catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
                {
                    sw.Stop();
                    return BuildPartialFailure(
                        "max_duration",
                        "Loop terminated: max_duration_seconds budget exhausted during tool call.",
                        sw.ElapsedMilliseconds,
                        conversation,
                        sink,
                        turn,
                        toolCalls,
                        tokensUsed,
                        model,
                        provider
                    );
                }
                catch (Exception ex)
                {
                    // Tool implementations are expected to swallow their own
                    // errors and return error-shaped JSON; landing here means
                    // a registry bug. Feed the message back so the model
                    // can recover, but log loudly.
                    _logger.LogWarning(
                        ex,
                        "Tool '{Tool}' threw on turn {Turn} (registry should return error envelope)",
                        call.Name,
                        turn
                    );
                    result = new { ok = false, error = ex.Message };
                }

                conversation.Add(
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)])
                );

                if (sink.FatalEncountered)
                {
                    sw.Stop();
                    return BuildPartialFailure(
                        "tool_error_unrecoverable",
                        $"Loop terminated: unrecoverable tool error. {sink.FatalReason}",
                        sw.ElapsedMilliseconds,
                        conversation,
                        sink,
                        turn,
                        toolCalls,
                        tokensUsed,
                        model,
                        provider
                    );
                }
            }

            turn++;

            if (tokensUsed >= maxTokens)
            {
                sw.Stop();
                return BuildPartialFailure(
                    "max_tokens",
                    $"Loop terminated: max_tokens budget exhausted ({tokensUsed} >= {maxTokens}).",
                    sw.ElapsedMilliseconds,
                    conversation,
                    sink,
                    turn,
                    toolCalls,
                    tokensUsed,
                    model,
                    provider
                );
            }
        }

        if (finalText is null && terminationReason == "max_steps")
        {
            // Hit the loop bound without the model declaring done.
            sw.Stop();
            return BuildPartialFailure(
                "max_steps",
                $"Loop terminated: max_steps budget exhausted ({maxSteps} turns) without a final answer.",
                sw.ElapsedMilliseconds,
                conversation,
                sink,
                turn,
                toolCalls,
                tokensUsed,
                model,
                provider
            );
        }

        sw.Stop();
        var (finalJson, parsedFromText) = TryParseFinalJson(finalText, responseFormatJson);
        var outputs = new Dictionary<string, object?>
        {
            ["final_text"] = finalText ?? string.Empty,
            ["final_json"] = finalJson,
            ["turns"] = turn,
            ["tool_calls"] = toolCalls,
            ["tokens_used"] = tokensUsed,
            ["cost_usd"] = (decimal?)null,
            ["model"] = model,
            ["provider"] = provider,
            ["termination_reason"] = terminationReason,
        };

        return new StepResult(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts:
            [
                new StepArtifact(
                    "transcript",
                    "transcript.json",
                    SerializeTranscript(conversation),
                    "application/json"
                ),
                new StepArtifact(
                    "tool_log",
                    "tool_log.json",
                    SerializeToolLog(sink),
                    "application/json"
                ),
            ],
            DurationMs: sw.ElapsedMilliseconds,
            TokensUsed: tokensUsed == 0 ? null : tokensUsed,
            CostUsd: null
        );
    }

    private StepResult BuildPartialFailure(
        string terminationReason,
        string errorMessage,
        long durationMs,
        IReadOnlyList<ChatMessage> conversation,
        ToolLogSink sink,
        int turn,
        long toolCalls,
        long tokensUsed,
        string model,
        string provider
    )
    {
        var outputs = new Dictionary<string, object?>
        {
            ["final_text"] = string.Empty,
            ["final_json"] = (object?)null,
            ["turns"] = turn,
            ["tool_calls"] = toolCalls,
            ["tokens_used"] = tokensUsed,
            ["cost_usd"] = (decimal?)null,
            ["model"] = model,
            ["provider"] = provider,
            ["termination_reason"] = terminationReason,
        };
        return new StepResult(
            Status: StepStatus.Failed,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts:
            [
                new StepArtifact(
                    "transcript",
                    "transcript.json",
                    SerializeTranscript(conversation),
                    "application/json"
                ),
                new StepArtifact(
                    "tool_log",
                    "tool_log.json",
                    SerializeToolLog(sink),
                    "application/json"
                ),
            ],
            DurationMs: durationMs,
            TokensUsed: tokensUsed == 0 ? null : tokensUsed,
            CostUsd: null,
            ErrorMessage: errorMessage
        );
    }

    private static List<FunctionCallContent> ExtractFunctionCalls(ChatResponse response)
    {
        var calls = new List<FunctionCallContent>();
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc)
                    calls.Add(fc);
            }
        }
        return calls;
    }

    private static string ExtractFinalAssistantText(ChatResponse response)
    {
        var lastAssistantText = response
            .Messages.Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .LastOrDefault();
        return lastAssistantText ?? response.Text ?? string.Empty;
    }

    private static long? ExtractTokenCount(ChatResponse response)
    {
        var usage = response.Usage;
        if (usage is null)
            return null;
        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        var total = input + output;
        return total == 0 ? null : total;
    }

    private static (object? Parsed, bool FromText) TryParseFinalJson(
        string? text,
        string? responseFormatJson
    )
    {
        if (string.IsNullOrWhiteSpace(responseFormatJson) || string.IsNullOrWhiteSpace(text))
            return (null, false);
        var trimmed = text.Trim();
        // Strip ```json / ``` fences when the model wraps its output.
        if (trimmed.StartsWith("```"))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0)
                trimmed = trimmed[(firstNl + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return (JsonSerializer.Deserialize<object?>(doc.RootElement.GetRawText()), true);
        }
        catch
        {
            return (null, false);
        }
    }

    private static byte[] SerializeTranscript(IReadOnlyList<ChatMessage> messages)
    {
        var entries = messages.Select(m => new
        {
            role = m.Role.Value,
            text = m.Text,
            tool_calls = m
                .Contents.OfType<FunctionCallContent>()
                .Select(fc => new
                {
                    call_id = fc.CallId,
                    name = fc.Name,
                    args = fc.Arguments,
                })
                .ToList(),
            tool_results = m
                .Contents.OfType<FunctionResultContent>()
                .Select(fr => new { call_id = fr.CallId, result = fr.Result })
                .ToList(),
        });
        return JsonSerializer.SerializeToUtf8Bytes(
            entries,
            new JsonSerializerOptions { WriteIndented = true }
        );
    }

    private static byte[] SerializeToolLog(ToolLogSink sink)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            sink.Entries,
            new JsonSerializerOptions { WriteIndented = true }
        );
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int? GetInt(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return null;
        return v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s
                when int.TryParse(
                    s,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var i
                ) => i,
            _ => null,
        };
    }

    private static long? GetLong(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return null;
        return v switch
        {
            int i => i,
            long l => l,
            double d => (long)d,
            string s
                when long.TryParse(
                    s,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var l
                ) => l,
            _ => null,
        };
    }

    private static TimeSpan? GetDurationSeconds(
        IReadOnlyDictionary<string, object?> inputs,
        string key
    )
    {
        var n = GetInt(inputs, key);
        return n.HasValue ? TimeSpan.FromSeconds(n.Value) : null;
    }

    private static List<string> ReadStringList(
        IReadOnlyDictionary<string, object?> inputs,
        string key
    )
    {
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return new();
        if (v is IEnumerable<object?> list)
            return [.. list.Select(o => o?.ToString() ?? string.Empty).Where(s => s.Length > 0)];
        if (v is string s)
            return
            [
                .. s.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                ),
            ];
        return new();
    }
}
