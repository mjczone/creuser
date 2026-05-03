using System.Diagnostics;
using System.Text.Json;
using Creuser.Agents;
using Creuser.Core.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// <c>type: llm-planner</c> step runner. Single-shot LLM call that emits
/// a structured <see cref="JobPlan"/> against the registered step type
/// catalog, persists it, and returns the plan id. Pairs with the
/// <see cref="JobExecutor"/>'s plan-then-execute path: when a single-step
/// job declares <c>type: llm-planner</c>, the executor walks the produced
/// plan as a continuation of the same run.
///
/// <para>
/// The runner does NOT auto-execute the plan itself — that's the
/// executor's job. Splitting the responsibilities keeps the runner contract
/// uniform (every step returns <see cref="StepResult"/>) and lets multi-step
/// DAG authors use the planner as a regular step whose outputs feed
/// downstream steps without auto-execution.
/// </para>
///
/// <para>
/// No tools — this is the deterministic-LLM seam. Agentic investigation
/// belongs in a downstream <c>llm-tool-loop</c> step that the planner can
/// emit. v1 ships read-only planning over a static step catalog; future
/// work injects the projection toolset so the planner can ground its
/// decisions in workspace shape before emitting steps.
/// </para>
/// </summary>
public sealed class LlmPlannerStepRunner : IStepRunner
{
    public string StepType => "llm-planner";

    private readonly IChatClientResolver _resolver;
    private readonly IJobPlanStore _plans;
    private readonly TimeProvider _time;
    private readonly ILogger<LlmPlannerStepRunner> _logger;

    public LlmPlannerStepRunner(
        IChatClientResolver resolver,
        IJobPlanStore plans,
        TimeProvider time,
        ILogger<LlmPlannerStepRunner> logger
    )
    {
        _resolver = resolver;
        _plans = plans;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Static step-type catalog the planner is taught about. Keep in sync
    /// with the registered <c>IStepRunner</c> set in DI; new runners that
    /// land here become available for the planner to emit. Plugin-contributed
    /// runners join via the post-v1 registry-introspection seam.
    /// </summary>
    private const string StepCatalog = """
        - `llm-chat` — Single-shot LLM completion. inputs: { prompt: str, system_prompt?: str, model?: str, response_format_json?: str }. outputs: { text, tokens_used }.
        - `llm-tool-loop` — Bounded ReAct loop. inputs: { goal: str, tools: [str], max_steps?: int, max_tokens?: int, max_duration_seconds?: int, response_format_json?: str }. outputs: { final_text, final_json, turns, tool_calls, tokens_used, termination_reason }.
        - `shell` — Shell command. inputs: { script: str }. The job's `allowed_commands` allow-list applies. outputs: { stdout, stderr, exit_code }.
        - `csharp` — Single-file C# script via `dotnet run`. inputs: { script: str }. outputs: { stdout, stderr, exit_code }.
        - `python` — Python script via `uv run` (PEP 723 inline deps supported). inputs: { script: str }. outputs: { stdout, stderr, exit_code }.
        - `node` — Node.js script. inputs: { script: str }. outputs: { stdout, stderr, exit_code }.
        - `file-mutate` — Declarative file ops. inputs: { ops: [{ op: "create"|"modify"|"delete"|"rename", path: str, content?: str, rename_to?: str }] }. outputs: { applied, paths }.
        - `file-frontmatter` — Multi-dialect frontmatter ops. inputs: { ops: [{ op: "set"|"unset"|"replace", path: str, frontmatter?: object, keys?: [str] }] }. outputs: { applied }.
        - `http` — HTTP request. inputs: { url: str, method?: str, headers?: object, query?: object, body?: any, body_type?: "json"|"form"|"text", parse?: "auto"|"json"|"text"|"none", expected_status?: [int], timeout_seconds?: int }. outputs: { status, headers, body, parsed, latency_ms, content_type, url }.
        - `projection-sync` — Re-scan workspace + rebuild entity projection. inputs: {}. outputs: { entities_total, entities_by_kind, refs_resolved, refs_unresolved, schema_failures, convention_count, convention_versions }.
        """;

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
                "llm-planner step requires a `goal` input. For single-step jobs the body of the script is moved into `goal` automatically; check the executor's binding step.",
                sw.ElapsedMilliseconds
            );
        }

        var providerOverride = GetString(inputs, "provider");
        var modelOverride = GetString(inputs, "model");
        var systemPromptOverride = GetString(inputs, "system_prompt");

        var resolution = await _resolver.ResolveAsync(providerOverride, modelOverride, ct);
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

        var systemPrompt = systemPromptOverride ?? BuildSystemPrompt();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, goal),
        };

        ChatResponse response;
        try
        {
            response = await resolution.Client.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = model },
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "llm-planner call failed for step {StepName}", ctx.StepName);
            return StepResult.Failure(
                $"Planner LLM call failed: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }

        var rawText = response.Text ?? string.Empty;
        var json = ExtractJsonBlock(rawText);
        if (string.IsNullOrWhiteSpace(json))
        {
            sw.Stop();
            return StepResult.Failure(
                "Planner returned no parseable JSON. Expected a `{ \"reasoning\": ..., \"steps\": [...] }` object.",
                sw.ElapsedMilliseconds
            );
        }

        PlannerOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PlannerOutput>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return StepResult.Failure(
                $"Planner output failed to parse as JSON: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }
        if (parsed is null || parsed.Steps is null || parsed.Steps.Count == 0)
        {
            sw.Stop();
            return StepResult.Failure(
                "Planner output had no `steps` array (or it was empty). Cannot continue.",
                sw.ElapsedMilliseconds
            );
        }

        var (planSteps, error) = ValidateAndConvertSteps(parsed.Steps);
        if (error is not null)
        {
            sw.Stop();
            return StepResult.Failure($"Plan validation failed: {error}", sw.ElapsedMilliseconds);
        }

        var tokensUsed = ExtractTokenCount(response);
        var planId = Guid.NewGuid();
        var stepsJson = JsonSerializer.Serialize(planSteps);
        var plan = new JobPlan(
            Id: planId,
            WorkspaceId: ctx.WorkspaceId,
            JobScriptId: Guid.Empty, // executor stamps the real script id when persisting
            Goal: goal,
            StepsJson: stepsJson,
            Reasoning: parsed.Reasoning,
            Model: model,
            Provider: provider,
            TokensUsed: tokensUsed,
            CreatedAt: _time.GetUtcNow().UtcDateTime
        );
        await _plans.SaveAsync(plan, ct);

        sw.Stop();

        // Sidecar artifact: the raw planner output for debugging "why did
        // it pick THESE steps?" cases.
        var transcript = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                provider,
                model,
                goal,
                system = systemPrompt,
                assistant = rawText,
                parsed,
            },
            new JsonSerializerOptions { WriteIndented = true }
        );

        var outputs = new Dictionary<string, object?>
        {
            ["plan_id"] = planId,
            ["step_count"] = planSteps!.Count,
            ["plan_step_ids"] = planSteps.Select(s => s.Id).ToList<object?>(),
            ["reasoning"] = parsed.Reasoning,
            ["model"] = model,
            ["provider"] = provider,
            ["tokens_used"] = tokensUsed,
        };

        return new StepResult(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts:
            [
                new StepArtifact("transcript", "transcript.json", transcript, "application/json"),
            ],
            DurationMs: sw.ElapsedMilliseconds,
            TokensUsed: tokensUsed
        );
    }

    private static string BuildSystemPrompt() =>
        $$"""
            You are a planning agent for the Creuser workflow platform. Given a user goal, you produce a structured `JobPlan`: an ordered list of steps that, when executed, accomplish the goal. Each step is a real platform runner that the executor will dispatch.

            # Available step types

            {{StepCatalog}}

            # Output format

            Emit STRICTLY a JSON object — no surrounding prose, no markdown fence required. Schema:

            ```
            {
              "reasoning": "1-3 sentence explanation of the plan's shape",
              "steps": [
                {
                  "id": "kebab-or-snake-case-id",
                  "name": "human-readable step name",
                  "type": "<one of the step types above>",
                  "depends_on": ["earlier-step-id", ...],
                  "inputs": { ... runner-specific inputs ... }
                },
                ...
              ]
            }
            ```

            # Rules

            - Step `id` values must be unique within the plan.
            - `depends_on` references must point at earlier steps' `id`s.
            - `inputs` references can use `$step_id.field` to reference an upstream step's output (the executor resolves these at runtime).
            - Prefer the smallest plan that accomplishes the goal. Don't add cleanup or summary steps that the user didn't ask for.
            - When the goal involves investigation, use `llm-tool-loop` with the `read_file` / `grep` / `find_files_by_pattern` / `list_directory` / `query_entities` / `find_orphans` tools.
            - When the goal involves writing files, end with a `file-mutate` step consuming the upstream investigation's structured output.
            - When the goal involves re-indexing the workspace, run `projection-sync` first so downstream steps see fresh entities.

            Return ONLY the JSON. No leading or trailing text.
            """;

    /// <summary>
    /// Pull a JSON object out of free-text LLM output. Models often wrap
    /// JSON in ```json fences; some embed it inline. This is a tolerant
    /// extractor — find the first <c>{</c> and balance braces to the
    /// matching close.
    /// </summary>
    private static string ExtractJsonBlock(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var start = text.IndexOf('{');
        if (start < 0)
            return string.Empty;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escape)
            {
                escape = false;
                continue;
            }
            if (ch == '\\' && inString)
            {
                escape = true;
                continue;
            }
            if (ch == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            if (ch == '{')
                depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }
        return string.Empty;
    }

    private static (List<JobPlanStep>? Steps, string? Error) ValidateAndConvertSteps(
        List<PlannerStep> raw
    )
    {
        // The implicit `planner` upstream is always available. Plan steps
        // can reference $planner.field for bindings, and depend on
        // "planner" to declare ordering. The executor injects a real
        // step decl with this id when it walks the plan.
        var seen = new HashSet<string>(StringComparer.Ordinal) { "planner" };
        var converted = new List<JobPlanStep>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            if (string.IsNullOrWhiteSpace(s.Id))
                return (null, $"steps[{i}] is missing `id`.");
            if (string.Equals(s.Id, "planner", StringComparison.Ordinal))
                return (
                    null,
                    $"steps[{i}] uses reserved id `planner`; the executor injects this for the planner step itself."
                );
            if (!seen.Add(s.Id))
                return (null, $"steps[{i}] id '{s.Id}' is duplicated.");
            if (string.IsNullOrWhiteSpace(s.Type))
                return (null, $"steps[{i}] (`{s.Id}`) is missing `type`.");

            converted.Add(
                new JobPlanStep(
                    Id: s.Id!,
                    Name: s.Name,
                    Type: s.Type!,
                    DependsOn: s.DependsOn ?? new List<string>(),
                    Inputs: s.Inputs ?? new Dictionary<string, object?>()
                )
            );
        }

        // Lightweight cycle / unknown-dep check: every depends_on must
        // appear in `seen`. The full Kahn validation runs again in the
        // executor when the plan is materialized into a DAG.
        foreach (var step in converted)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!seen.Contains(dep))
                    return (
                        null,
                        $"step '{step.Id}' depends on '{dep}' which is not declared in the plan."
                    );
                if (string.Equals(dep, step.Id, StringComparison.Ordinal))
                    return (null, $"step '{step.Id}' depends on itself.");
            }
        }

        return (converted, null);
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

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;

    private sealed class PlannerOutput
    {
        public string? Reasoning { get; set; }
        public List<PlannerStep>? Steps { get; set; }
    }

    private sealed class PlannerStep
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("depends_on")]
        public List<string>? DependsOn { get; set; }
        public Dictionary<string, object?>? Inputs { get; set; }
    }
}
