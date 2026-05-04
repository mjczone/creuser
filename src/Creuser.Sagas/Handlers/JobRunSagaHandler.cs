using System.Diagnostics;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Sagas.Commands;
using Creuser.Sagas.Events;
using Creuser.Scripting;
using Marten;
using Microsoft.Extensions.Logging;

namespace Creuser.Sagas.Handlers;

/// <summary>
/// Wolverine handlers for the run-orchestration saga. State lives as a
/// Marten document (<see cref="JobRunSagaState"/>) keyed by run id; each
/// handler loads the doc, mutates it, persists, and returns cascading
/// messages for the next dispatch wave (or signals run completion).
///
/// <para>
/// One handler class per command type — Wolverine discovers them by name
/// (<c>Handle</c>). The saga state document is loaded inside each handler
/// so each invocation is self-contained; Wolverine's outbox pattern
/// ensures cascading messages + the saga doc commit happen in one
/// Marten transaction.
/// </para>
///
/// <para>
/// All three execution patterns flow through this single saga:
/// <list type="number">
/// <item><b>Single-step</b> — <c>StartJobRun</c> initializes <see cref="JobRunSagaState.StepsJson"/> with one step and dispatches it; <c>StepCompleted</c> finishes the run.</item>
/// <item><b>Multi-step DAG</b> — <c>StartJobRun</c> initializes with the validated DAG, dispatches root steps; each <c>StepCompleted</c> dispatches newly-ready dependents; <c>StepFailed</c> cancels transitive dependents.</item>
/// <item><b>Plan-then-execute</b> — <c>StartJobRun</c> initializes with the planner step; on <c>StepCompleted</c> for the planner, the saga loads the plan, hydrates plan steps, and continues.</item>
/// </list>
/// </para>
/// </summary>
public static class JobRunSagaHandler
{
    /// <summary>
    /// Entry point — handles the externally-published <see cref="StartJobRun"/>
    /// command. Loads the script + workspace, builds the initial saga
    /// state, persists the initial <see cref="JobRun"/>, appends a
    /// <see cref="JobRunStarted"/> event, and returns the first wave of
    /// <see cref="DispatchStep"/> messages.
    /// </summary>
    public static async Task<IEnumerable<object>> Handle(
        StartJobRun cmd,
        IDocumentSession session,
        IJobScriptStore scripts,
        IJobRunStore runs,
        IWorkspaceStore workspaces,
        IWorkspaceWorkingTree workingTree,
        TimeProvider time,
        ILogger<StartJobRunHandlerLog> logger,
        CancellationToken ct
    )
    {
        var script = await scripts.FindByIdAsync(cmd.JobScriptId, ct);
        if (script is null)
        {
            logger.LogError("Job script {JobScriptId} not found at run start", cmd.JobScriptId);
            // Persist a failed run record so the audit trail shows the failure.
            await PersistFailedRunAsync(
                runs,
                cmd,
                $"Job script {cmd.JobScriptId} not found.",
                time,
                ct
            );
            return [new JobRunFinished(cmd.RunId, JobRunStatus.Failed, "Job script not found.")];
        }

        var workspace = await workspaces.FindByIdAsync(script.WorkspaceId, ct);
        if (workspace is null)
        {
            await PersistFailedRunAsync(
                runs,
                cmd,
                $"Workspace {script.WorkspaceId} no longer exists.",
                time,
                ct
            );
            return [new JobRunFinished(cmd.RunId, JobRunStatus.Failed, "Workspace not found.")];
        }

        var workingTreePath = await workingTree.ResolvePathAsync(workspace, ct) ?? string.Empty;
        var startSha = string.IsNullOrEmpty(workingTreePath)
            ? null
            : await workingTree.ResolveHeadShaAsync(workspace, workingTreePath, ct);

        var startedAt = time.GetUtcNow().UtcDateTime;
        var initialRun = new JobRun(
            Id: cmd.RunId,
            JobScriptId: script.Id,
            WorkspaceId: script.WorkspaceId,
            Status: JobRunStatus.Running,
            ParametersJson: JsonSerializer.Serialize(cmd.Parameters),
            StartCommitSha: startSha,
            EndCommitSha: null,
            StartedAt: startedAt,
            CompletedAt: null,
            TriggeredBy: cmd.TriggeredBy,
            TriggerKind: cmd.TriggerKind,
            PredecessorRunId: null,
            PlanId: null,
            FailureMessage: null,
            TotalTokensUsed: null,
            TotalCostUsd: null,
            DurationMs: 0
        );
        await runs.SaveRunAsync(initialRun, ct);

        var frontmatter = FrontmatterParser.ParseFrontmatter(script.Frontmatter);
        var normalizedParams = InputsNormalizer.NormalizeRoot(cmd.Parameters);
        var steps = BuildInitialSteps(frontmatter, script);

        // DAG validation upfront when steps are explicitly declared.
        if (steps.Count > 1)
        {
            var validation = DagValidator.Validate(steps);
            if (validation.Error is not null)
            {
                await PersistDagValidationFailureAsync(runs, cmd, validation.Error, time, ct);
                return [new JobRunFinished(cmd.RunId, JobRunStatus.Failed, validation.Error)];
            }
            steps = validation.Sorted.ToList();
        }

        var state = new JobRunSagaState
        {
            Id = cmd.RunId,
            JobScriptId = script.Id,
            WorkspaceId = workspace.Id,
            WorkspaceSlug = workspace.Slug,
            WorkingTreePath = workingTreePath,
            TriggerKind = cmd.TriggerKind,
            TriggeredBy = cmd.TriggeredBy,
            Status = "running",
            StartedAt = startedAt,
            StartCommitSha = startSha,
            StepsJson = JsonSerializer.Serialize(steps),
            ParametersJson = JsonSerializer.Serialize(normalizedParams),
            NextPosition = 0,
        };

        // Append run-started event.
        session.Events.StartStream(
            cmd.RunId,
            new JobRunStarted(
                cmd.RunId,
                script.Id,
                workspace.Id,
                cmd.TriggerKind,
                cmd.TriggeredBy,
                state.ParametersJson,
                startSha,
                startedAt
            )
        );

        var (dispatches, newState, bindingFailures) = DispatchReadyStepsWithBindingFailures(
            state,
            frontmatter,
            time
        );
        await PersistBindingFailuresAsync(state, bindingFailures, runs, session, time, ct);
        session.Store(newState);
        await session.SaveChangesAsync(ct);

        if (dispatches.Count == 0)
        {
            // Nothing to dispatch — either empty steps list or every
            // root step failed at binding resolution. Finalize.
            if (bindingFailures.Count > 0)
                return await FinalizeAndSignalAsync(newState, runs, session, time, ct);
            return [new JobRunFinished(cmd.RunId, JobRunStatus.Succeeded, null)];
        }

        return dispatches.Cast<object>().ToList();
    }

    /// <summary>
    /// Step-completion handler. Updates saga state, dispatches newly-ready
    /// dependents, and finalizes the run when no work remains.
    /// </summary>
    public static async Task<IEnumerable<object>> Handle(
        StepCompleted msg,
        IDocumentSession session,
        IJobScriptStore scripts,
        IJobRunStore runs,
        IJobPlanStore plans,
        TimeProvider time,
        ILogger<StepCompletedHandlerLog> logger,
        CancellationToken ct
    )
    {
        var state = await session.LoadAsync<JobRunSagaState>(msg.RunId, ct);
        if (state is null)
        {
            logger.LogWarning(
                "StepCompleted for unknown run {RunId} (saga state missing)",
                msg.RunId
            );
            return Array.Empty<object>();
        }

        // Update saga state with step's results.
        state.StepStatuses[msg.StepDeclId] = "succeeded";
        state.StepOutputsJson[msg.StepDeclId] = msg.OutputsJson;
        if (msg.CommitSha is not null)
            state.LastCommitSha = msg.CommitSha;
        if (msg.TokensUsed is { } tokens)
            state.TotalTokensUsed = (state.TotalTokensUsed ?? 0) + tokens;
        if (msg.CostUsd is { } cost)
            state.TotalCostUsd = (state.TotalCostUsd ?? 0m) + cost;

        session.Events.Append(
            msg.RunId,
            new JobRunStepCompleted(
                msg.StepId,
                msg.RunId,
                msg.StepDeclId,
                msg.OutputsJson,
                msg.FileChangeCount,
                msg.CommitSha,
                msg.TokensUsed,
                msg.CostUsd,
                msg.DurationMs,
                time.GetUtcNow().UtcDateTime
            )
        );

        // Plan-then-execute: when the planner step completes, hydrate the
        // saga with plan steps before computing next dispatches.
        var script = await scripts.FindByIdAsync(state.JobScriptId, ct);
        var frontmatter = script is null
            ? new JobScriptFrontmatter()
            : FrontmatterParser.ParseFrontmatter(script.Frontmatter);

        if (
            !state.PlannerHydrated
            && msg.StepDeclId == "planner"
            && string.Equals(frontmatter.Type, "llm-planner", StringComparison.Ordinal)
        )
        {
            var hydrated = await TryHydratePlanAsync(
                state,
                msg.OutputsJson,
                plans,
                script!.Id,
                session,
                time,
                logger,
                ct
            );
            if (!hydrated.Ok)
            {
                state.Status = "failed";
                state.FailureMessage = hydrated.Error;
                session.Store(state);
                await FinalizeRunAsync(state, runs, session, time, ct);
                return [new JobRunFinished(msg.RunId, JobRunStatus.Failed, hydrated.Error)];
            }
            state = hydrated.NewState!;
        }

        var (dispatches, newState, bindingFailures) = DispatchReadyStepsWithBindingFailures(
            state,
            frontmatter,
            time
        );
        await PersistBindingFailuresAsync(state, bindingFailures, runs, session, time, ct);
        if (dispatches.Count > 0)
        {
            session.Store(newState);
            await session.SaveChangesAsync(ct);
            return dispatches.Cast<object>().ToList();
        }

        // No new dispatches — but are any steps still in-flight? Wolverine
        // dispatches concurrently, so a fast step can complete while
        // siblings are still running. Don't finalize until all steps have
        // terminated (succeeded / failed / cancelled).
        if (HasInFlightSteps(newState))
        {
            session.Store(newState);
            await session.SaveChangesAsync(ct);
            return Array.Empty<object>();
        }

        return await FinalizeAndSignalAsync(newState, runs, session, time, ct);
    }

    /// <summary>
    /// Step-failure handler. Marks the failed step, cancels transitive
    /// dependents, and finalizes the run as failed once any concurrent
    /// in-flight siblings drain.
    /// </summary>
    public static async Task<IEnumerable<object>> Handle(
        StepFailed msg,
        IDocumentSession session,
        IJobScriptStore scripts,
        IJobRunStore runs,
        TimeProvider time,
        ILogger<StepFailedHandlerLog> logger,
        CancellationToken ct
    )
    {
        var state = await session.LoadAsync<JobRunSagaState>(msg.RunId, ct);
        if (state is null)
        {
            logger.LogWarning("StepFailed for unknown run {RunId} (saga state missing)", msg.RunId);
            return Array.Empty<object>();
        }

        state.StepStatuses[msg.StepDeclId] = "failed";
        state.FailureMessage ??= msg.ErrorMessage;
        state.Status = "failed";

        session.Events.Append(
            msg.RunId,
            new JobRunStepFailed(
                msg.StepId,
                msg.RunId,
                msg.StepDeclId,
                msg.ErrorMessage,
                msg.DurationMs,
                time.GetUtcNow().UtcDateTime
            )
        );

        // Cancel transitive dependents (mark as Cancelled in audit).
        var steps = state.DeserializeSteps();
        await CancelDependentsAsync(state, steps, msg.StepDeclId, runs, session, time, ct);

        // If concurrent independent steps are still in-flight, wait for
        // them to complete before finalizing the run. The run still ends
        // up Failed (state.Status is already "failed" + at least one step
        // is failed), but waiting lets the audit timeline include the
        // independent siblings' final outcomes.
        if (HasInFlightSteps(state))
        {
            session.Store(state);
            await session.SaveChangesAsync(ct);
            return Array.Empty<object>();
        }

        return await FinalizeAndSignalAsync(state, runs, session, time, ct);
    }

    /// <summary>
    /// True when any step still has a non-terminal status (dispatched but
    /// not yet completed/failed/cancelled). Pre-dispatch states (no entry
    /// in <see cref="JobRunSagaState.StepStatuses"/>) count too — those are
    /// blocked-pending, awaiting upstream completion.
    /// </summary>
    private static bool HasInFlightSteps(JobRunSagaState state)
    {
        var steps = state.DeserializeSteps();
        foreach (var step in steps)
        {
            if (!state.StepStatuses.TryGetValue(step.Id, out var status))
            {
                // Pre-dispatch: only counts as "in flight" if its
                // dependencies are still running too. If a dep is failed
                // and we haven't cancelled this step yet, it's about to
                // be cancelled — count as in-flight to give the failure
                // path a chance to mark it.
                if (
                    step.DependsOn.Any(dep =>
                        state.StepStatuses.GetValueOrDefault(dep) is "dispatched" or "succeeded"
                    )
                )
                    return true;
                continue;
            }
            if (string.Equals(status, "dispatched", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Build the initial step list. Single-step jobs synthesize one step
    /// from the legacy <c>type:</c> + body shape; multi-step jobs use
    /// <c>frontmatter.Steps</c>; planner jobs synthesize a planner step at
    /// the head and add the rest after the planner step completes.
    /// </summary>
    private static List<JobScriptStepDecl> BuildInitialSteps(
        JobScriptFrontmatter frontmatter,
        JobScript script
    )
    {
        if (frontmatter.Steps.Count > 0)
        {
            return frontmatter
                .Steps.Select(s => new JobScriptStepDecl
                {
                    Id = s.Id,
                    Name = s.Name,
                    Type = s.Type,
                    DependsOn = s.DependsOn.ToList(),
                    Inputs = s.Inputs.ToDictionary(kv => kv.Key, kv => kv.Value),
                })
                .ToList();
        }

        // Single-step or planner — one synthetic step using the body
        // substitution. Planner triggers hydration on completion.
        var inputs = BuildSingleStepInputs(frontmatter, script.Body);
        var stepName = string.Equals(frontmatter.Type, "llm-planner", StringComparison.Ordinal)
            ? "planner"
            : script.Name;
        var stepId = string.Equals(frontmatter.Type, "llm-planner", StringComparison.Ordinal)
            ? "planner"
            : "main";
        return
        [
            new JobScriptStepDecl
            {
                Id = stepId,
                Name = stepName,
                Type = frontmatter.Type,
                DependsOn = new List<string>(),
                Inputs = inputs.ToDictionary(kv => kv.Key, kv => kv.Value),
            },
        ];
    }

    private static IReadOnlyDictionary<string, object?> BuildSingleStepInputs(
        JobScriptFrontmatter frontmatter,
        string body
    )
    {
        var merged = new Dictionary<string, object?>();
        foreach (var (k, v) in frontmatter.Inputs)
            merged[k] = v;

        if (frontmatter.Type == "llm-chat" && !merged.ContainsKey("prompt"))
            merged["prompt"] = body;
        if (frontmatter.Type == "llm-tool-loop" && !merged.ContainsKey("goal"))
            merged["goal"] = body;
        if (frontmatter.Type == "llm-planner" && !merged.ContainsKey("goal"))
            merged["goal"] = body;
        if (frontmatter.Type == "shell" && !merged.ContainsKey("script"))
            merged["script"] = body;
        if (frontmatter.Type == "csharp" && !merged.ContainsKey("script"))
            merged["script"] = body;
        if (frontmatter.Type == "python" && !merged.ContainsKey("script"))
            merged["script"] = body;
        if (frontmatter.Type == "node" && !merged.ContainsKey("script"))
            merged["script"] = body;

        return InputsNormalizer.NormalizeRoot(merged);
    }

    /// <summary>
    /// Compute the next wave of dispatches from current saga state. A step
    /// is dispatchable when:
    /// <list type="bullet">
    /// <item>It hasn't been dispatched yet (no entry in <see cref="JobRunSagaState.StepStatuses"/>).</item>
    /// <item>All its <c>depends_on</c> entries are in succeeded status.</item>
    /// </list>
    /// Steps blocked by a failed/cancelled upstream get marked Cancelled
    /// inline (persisted via <c>SaveStepAsync</c> + a JobRunStepCancelled
    /// event). Steps that fail at binding resolution get persisted as
    /// failed step records so the audit timeline captures the failure
    /// (the runner never ran). The returned dispatches are the
    /// actually-runnable next wave.
    /// </summary>
    private static (
        List<DispatchStep> Dispatches,
        JobRunSagaState NewState,
        IReadOnlyList<FailedBinding> BindingFailures
    ) DispatchReadyStepsWithBindingFailures(
        JobRunSagaState state,
        JobScriptFrontmatter frontmatter,
        TimeProvider time
    )
    {
        var steps = state.DeserializeSteps();
        var dispatches = new List<DispatchStep>();
        var bindingFailures = new List<FailedBinding>();

        foreach (var step in steps)
        {
            if (state.StepStatuses.ContainsKey(step.Id))
                continue;

            // Cancellation propagation — any upstream failed or cancelled?
            var blockedBy = step.DependsOn.FirstOrDefault(dep =>
                state.StepStatuses.GetValueOrDefault(dep) is "failed" or "cancelled"
            );
            if (blockedBy is not null)
            {
                state.StepStatuses[step.Id] = "cancelled";
                // Cancellation persistence happens in CancelDependentsAsync
                // when StepFailed fires; here we just mark state and skip.
                continue;
            }

            // Are all dependencies succeeded?
            if (
                !step.DependsOn.All(dep =>
                    string.Equals(
                        state.StepStatuses.GetValueOrDefault(dep),
                        "succeeded",
                        StringComparison.Ordinal
                    )
                )
            )
                continue;

            // Resolve bindings.
            IReadOnlyDictionary<string, object?> resolvedInputs;
            try
            {
                var normalizedDeclared = InputsNormalizer.NormalizeRoot(step.Inputs);
                resolvedInputs = StepBindingResolver.Resolve(
                    normalizedDeclared,
                    state.DeserializeOutputs(),
                    state.DeserializeParameters()
                );
            }
            catch (StepBindingException ex)
            {
                state.StepStatuses[step.Id] = "failed";
                state.FailureMessage ??= ex.Message;
                state.Status = "failed";
                var rowId = Guid.NewGuid();
                var position = state.NextPosition++;
                state.StepRowIds[step.Id] = rowId;
                state.StepPositions[step.Id] = position;
                bindingFailures.Add(new FailedBinding(step, rowId, position, ex.Message));
                continue;
            }

            var stepId = Guid.NewGuid();
            var position2 = state.NextPosition++;
            state.StepRowIds[step.Id] = stepId;
            state.StepPositions[step.Id] = position2;
            state.StepStatuses[step.Id] = "dispatched";

            dispatches.Add(
                new DispatchStep(
                    RunId: state.Id,
                    StepId: stepId,
                    StepDeclId: step.Id,
                    Position: position2,
                    StepType: step.Type,
                    StepName: string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name,
                    WorkspaceId: state.WorkspaceId,
                    WorkspaceSlug: state.WorkspaceSlug,
                    WorkingTreePath: state.WorkingTreePath,
                    Inputs: resolvedInputs,
                    AllowedCommands: frontmatter.AllowedCommands,
                    RequiredSecrets: frontmatter.RequiredSecrets,
                    BudgetMaxDurationSeconds: frontmatter.Budgets?.MaxDurationSeconds,
                    BudgetMaxTokens: frontmatter.Budgets?.MaxTokens,
                    BudgetMaxCostUsd: frontmatter.Budgets?.MaxCostUsd
                )
            );
        }

        return (dispatches, state, bindingFailures);
    }

    /// <summary>Backward-compat overload — discards binding failures.</summary>
    private static (List<DispatchStep> Dispatches, JobRunSagaState NewState) DispatchReadySteps(
        JobRunSagaState state,
        JobScriptFrontmatter frontmatter,
        TimeProvider time
    )
    {
        var (dispatches, newState, _) = DispatchReadyStepsWithBindingFailures(
            state,
            frontmatter,
            time
        );
        return (dispatches, newState);
    }

    private sealed record FailedBinding(
        JobScriptStepDecl Step,
        Guid RowId,
        int Position,
        string Error
    );

    private sealed record HydrateResult(bool Ok, JobRunSagaState? NewState, string? Error);

    /// <summary>
    /// After the planner step completes, fetch the persisted JobPlan,
    /// synthesize a step list (planner at head + plan steps following),
    /// and update saga state. Plan steps with no <c>depends_on</c> get an
    /// implicit dependency on the planner so they don't sort ahead of it.
    /// </summary>
    private static async Task<HydrateResult> TryHydratePlanAsync(
        JobRunSagaState state,
        string plannerOutputsJson,
        IJobPlanStore plans,
        Guid jobScriptId,
        IDocumentSession session,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct
    )
    {
        // Pull plan_id from the planner step's outputs.
        Guid planId;
        try
        {
            using var doc = JsonDocument.Parse(plannerOutputsJson);
            if (
                !doc.RootElement.TryGetProperty("plan_id", out var planIdProp)
                || planIdProp.ValueKind == JsonValueKind.Null
            )
                return new HydrateResult(false, null, "Planner step did not return a `plan_id`.");
            planId = planIdProp.GetGuid();
        }
        catch (Exception ex)
        {
            return new HydrateResult(
                false,
                null,
                $"Planner outputs were not parseable JSON: {ex.Message}"
            );
        }

        var plan = await plans.FindByIdAsync(planId, ct);
        if (plan is null)
            return new HydrateResult(
                false,
                null,
                $"Planner persisted plan {planId} but it could not be loaded for execution."
            );

        // Stamp the script id back on the plan now we know it.
        if (plan.JobScriptId == Guid.Empty)
            await plans.SaveAsync(plan with { JobScriptId = jobScriptId }, ct);

        IReadOnlyList<JobPlanStep> planSteps;
        try
        {
            planSteps =
                JsonSerializer.Deserialize<List<JobPlanStep>>(plan.StepsJson)
                ?? new List<JobPlanStep>();
        }
        catch (Exception ex)
        {
            return new HydrateResult(
                false,
                null,
                $"Failed to deserialize plan {planId} steps: {ex.Message}"
            );
        }

        var steps = state.DeserializeSteps().ToList();
        foreach (var ps in planSteps)
        {
            var deps = ps.DependsOn.ToList();
            if (deps.Count == 0)
                deps.Add("planner");
            steps.Add(
                new JobScriptStepDecl
                {
                    Id = ps.Id,
                    Name = ps.Name,
                    Type = ps.Type,
                    DependsOn = deps,
                    Inputs = ps.Inputs.ToDictionary(kv => kv.Key, kv => kv.Value),
                }
            );
        }

        // Validate the synthesized DAG.
        var validation = DagValidator.Validate(steps);
        if (validation.Error is not null)
            return new HydrateResult(
                false,
                null,
                $"Plan DAG validation failed: {validation.Error}"
            );

        state.StepsJson = JsonSerializer.Serialize(validation.Sorted);
        state.PlanId = planId;
        state.PlannerHydrated = true;

        session.Events.Append(
            state.Id,
            new JobPlanLoaded(state.Id, planId, planSteps.Count, time.GetUtcNow().UtcDateTime)
        );

        return new HydrateResult(true, state, null);
    }

    /// <summary>
    /// Persist Cancelled JobRunStep records for every step transitively
    /// downstream of <paramref name="failedStepId"/>. Mirrors the existing
    /// <c>JobExecutor.PersistCancelledStepAsync</c> exactly so the audit
    /// timeline shape is preserved.
    /// </summary>
    private static async Task CancelDependentsAsync(
        JobRunSagaState state,
        IReadOnlyList<JobScriptStepDecl> steps,
        string failedStepId,
        IJobRunStore runs,
        IDocumentSession session,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var now = time.GetUtcNow().UtcDateTime;
        // Find transitive dependents of the failed step.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(failedStepId);
        while (queue.TryDequeue(out var current))
        {
            foreach (var s in steps)
            {
                if (s.DependsOn.Contains(current, StringComparer.Ordinal) && blocked.Add(s.Id))
                    queue.Enqueue(s.Id);
            }
        }

        foreach (var stepId in blocked)
        {
            // Skip steps already in a terminal status.
            if (
                state.StepStatuses.GetValueOrDefault(stepId)
                is "succeeded"
                    or "failed"
                    or "cancelled"
            )
                continue;
            state.StepStatuses[stepId] = "cancelled";
            var stepDecl = steps.First(s => s.Id == stepId);
            var rowId = state.StepRowIds.GetValueOrDefault(stepId, Guid.NewGuid());
            var position = state.StepPositions.GetValueOrDefault(stepId, state.NextPosition++);
            state.StepRowIds[stepId] = rowId;
            state.StepPositions[stepId] = position;

            await runs.SaveStepAsync(
                new JobRunStep(
                    Id: rowId,
                    RunId: state.Id,
                    Position: position,
                    StepType: stepDecl.Type,
                    Name: string.IsNullOrWhiteSpace(stepDecl.Name) ? stepDecl.Id : stepDecl.Name,
                    Status: StepStatus.Cancelled,
                    IdempotencyKey: Sha256("cancelled|" + state.JobScriptId + "|" + stepId),
                    CachedFromStepId: null,
                    InputsJson: JsonSerializer.Serialize(stepDecl.Inputs),
                    OutputsJson: null,
                    InputsHash: Sha256(JsonSerializer.Serialize(stepDecl.Inputs)),
                    FileChangeCount: 0,
                    CommitSha: null,
                    StartedAt: now,
                    CompletedAt: now,
                    DurationMs: 0,
                    TokensUsed: null,
                    CostUsd: null,
                    ErrorMessage: $"Cancelled — upstream step '{failedStepId}' did not succeed.",
                    ResumeToken: null
                ),
                ct
            );

            session.Events.Append(
                state.Id,
                new JobRunStepCancelled(rowId, state.Id, stepId, failedStepId, now)
            );
        }
    }

    /// <summary>
    /// Run reached a terminal state — persist the final <see cref="JobRun"/>,
    /// append <see cref="JobRunCompleted"/> event, save the saga doc one
    /// last time. Returns the <see cref="JobRunFinished"/> message that
    /// signals the synchronous-endpoint waiter.
    /// </summary>
    private static async Task<IEnumerable<object>> FinalizeAndSignalAsync(
        JobRunSagaState state,
        IJobRunStore runs,
        IDocumentSession session,
        TimeProvider time,
        CancellationToken ct
    )
    {
        await FinalizeRunAsync(state, runs, session, time, ct);
        await session.SaveChangesAsync(ct);
        var status = state.Status switch
        {
            "failed" => JobRunStatus.Failed,
            "cancelled" => JobRunStatus.Cancelled,
            _ => JobRunStatus.Succeeded,
        };
        return [new JobRunFinished(state.Id, status, state.FailureMessage)];
    }

    private static async Task FinalizeRunAsync(
        JobRunSagaState state,
        IJobRunStore runs,
        IDocumentSession session,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var existing = await runs.FindByIdAsync(state.Id, ct);
        if (existing is null)
            return;

        var anyFailed = state.StepStatuses.Values.Any(s => s == "failed");
        var anyCancelled = !anyFailed && state.StepStatuses.Values.Any(s => s == "cancelled");
        var status =
            anyFailed ? JobRunStatus.Failed
            : anyCancelled ? JobRunStatus.Cancelled
            : JobRunStatus.Succeeded;

        var completedAt = time.GetUtcNow().UtcDateTime;
        var endSha = state.LastCommitSha ?? state.StartCommitSha;
        var durationMs = (long)(completedAt - state.StartedAt).TotalMilliseconds;

        var final = existing with
        {
            Status = status,
            CompletedAt = completedAt,
            EndCommitSha = endSha,
            PlanId = state.PlanId,
            FailureMessage = state.FailureMessage,
            TotalTokensUsed = state.TotalTokensUsed,
            TotalCostUsd = state.TotalCostUsd,
            DurationMs = durationMs,
        };
        await runs.SaveRunAsync(final, ct);

        session.Events.Append(
            state.Id,
            new JobRunCompleted(
                state.Id,
                status.ToString().ToLowerInvariant(),
                state.FailureMessage,
                endSha,
                state.PlanId,
                state.TotalTokensUsed,
                state.TotalCostUsd,
                durationMs,
                completedAt
            )
        );
    }

    /// <summary>
    /// Persist a Failed JobRunStep record per binding-resolution failure
    /// (typo in $step.field, missing parameter, etc.). The step never ran
    /// but the audit timeline must show why downstream stalled.
    /// </summary>
    private static async Task PersistBindingFailuresAsync(
        JobRunSagaState state,
        IReadOnlyList<FailedBinding> failures,
        IJobRunStore runs,
        IDocumentSession session,
        TimeProvider time,
        CancellationToken ct
    )
    {
        if (failures.Count == 0)
            return;
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var failure in failures)
        {
            var stepDecl = failure.Step;
            await runs.SaveStepAsync(
                new JobRunStep(
                    Id: failure.RowId,
                    RunId: state.Id,
                    Position: failure.Position,
                    StepType: stepDecl.Type,
                    Name: string.IsNullOrWhiteSpace(stepDecl.Name) ? stepDecl.Id : stepDecl.Name,
                    Status: StepStatus.Failed,
                    IdempotencyKey: Sha256("binding|" + state.JobScriptId + "|" + stepDecl.Id),
                    CachedFromStepId: null,
                    InputsJson: JsonSerializer.Serialize(stepDecl.Inputs),
                    OutputsJson: null,
                    InputsHash: Sha256(JsonSerializer.Serialize(stepDecl.Inputs)),
                    FileChangeCount: 0,
                    CommitSha: null,
                    StartedAt: now,
                    CompletedAt: now,
                    DurationMs: 0,
                    TokensUsed: null,
                    CostUsd: null,
                    ErrorMessage: failure.Error,
                    ResumeToken: null
                ),
                ct
            );
            session.Events.Append(
                state.Id,
                new JobRunStepFailed(failure.RowId, state.Id, stepDecl.Id, failure.Error, 0, now)
            );
        }
    }

    private static async Task PersistFailedRunAsync(
        IJobRunStore runs,
        StartJobRun cmd,
        string error,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var now = time.GetUtcNow().UtcDateTime;
        await runs.SaveRunAsync(
            new JobRun(
                Id: cmd.RunId,
                JobScriptId: cmd.JobScriptId,
                WorkspaceId: Guid.Empty,
                Status: JobRunStatus.Failed,
                ParametersJson: JsonSerializer.Serialize(cmd.Parameters),
                StartCommitSha: null,
                EndCommitSha: null,
                StartedAt: now,
                CompletedAt: now,
                TriggeredBy: cmd.TriggeredBy,
                TriggerKind: cmd.TriggerKind,
                PredecessorRunId: null,
                PlanId: null,
                FailureMessage: error,
                TotalTokensUsed: null,
                TotalCostUsd: null,
                DurationMs: 0
            ),
            ct
        );
    }

    private static async Task PersistDagValidationFailureAsync(
        IJobRunStore runs,
        StartJobRun cmd,
        string error,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var now = time.GetUtcNow().UtcDateTime;
        await runs.SaveRunAsync(
            new JobRun(
                Id: cmd.RunId,
                JobScriptId: cmd.JobScriptId,
                WorkspaceId: Guid.Empty,
                Status: JobRunStatus.Failed,
                ParametersJson: JsonSerializer.Serialize(cmd.Parameters),
                StartCommitSha: null,
                EndCommitSha: null,
                StartedAt: now,
                CompletedAt: now,
                TriggeredBy: cmd.TriggeredBy,
                TriggerKind: cmd.TriggerKind,
                PredecessorRunId: null,
                PlanId: null,
                FailureMessage: error,
                TotalTokensUsed: null,
                TotalCostUsd: null,
                DurationMs: 0
            ),
            ct
        );
        await runs.SaveStepAsync(
            new JobRunStep(
                Id: Guid.NewGuid(),
                RunId: cmd.RunId,
                Position: 0,
                StepType: "_dag_validation",
                Name: "DAG validation",
                Status: StepStatus.Failed,
                IdempotencyKey: Sha256("_dag_validation|" + cmd.JobScriptId),
                CachedFromStepId: null,
                InputsJson: "{}",
                OutputsJson: null,
                InputsHash: Sha256("_dag_validation"),
                FileChangeCount: 0,
                CommitSha: null,
                StartedAt: now,
                CompletedAt: now,
                DurationMs: 0,
                TokensUsed: null,
                CostUsd: null,
                ErrorMessage: error,
                ResumeToken: null
            ),
            ct
        );
    }

    private static string Sha256(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Marker types for ILogger generics — keeps the log scopes named.</summary>
    public sealed class StartJobRunHandlerLog;

    public sealed class StepCompletedHandlerLog;

    public sealed class StepFailedHandlerLog;
}
