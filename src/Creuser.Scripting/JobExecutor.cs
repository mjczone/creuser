using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// In-process synchronous executor — the v0.1 implementation of
/// "run a job." Walks the (currently single-step) job, dispatches to the
/// appropriate <see cref="IStepRunner"/>, persists the run + per-step audit
/// records, and returns the final <see cref="JobRun"/>.
///
/// <para>
/// The Wolverine-saga-driven executor that lands in v1.x exposes the same
/// surface (<see cref="ExecuteAsync(Guid, IReadOnlyDictionary{string, object?}, Guid?, string, CancellationToken)"/>)
/// — only the dispatch mechanism changes (durable messages instead of
/// in-process await). The <see cref="IStepRunner"/> contract doesn't move,
/// so step implementations are migration-agnostic.
/// </para>
///
/// <para>
/// File mutation discipline: steps return <see cref="FileChange"/>; this
/// executor stages and commits them transactionally per step (when the step
/// touches files). v0.1 first slice has no file-mutating runner yet, so
/// the file-mutation path is implemented but exercised in subsequent
/// passes (`shell`, `csharp`, `file-mutate`, `file-frontmatter`).
/// </para>
/// </summary>
public sealed class JobExecutor
{
    private readonly IJobScriptStore _scripts;
    private readonly IJobRunStore _runs;
    private readonly IWorkspaceStore _workspaces;
    private readonly IWorkspaceWorkingTree _workingTree;
    private readonly IJobPlanStore _plans;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<JobExecutor> _logger;

    public JobExecutor(
        IJobScriptStore scripts,
        IJobRunStore runs,
        IWorkspaceStore workspaces,
        IWorkspaceWorkingTree workingTree,
        IJobPlanStore plans,
        IServiceProvider services,
        TimeProvider time,
        ILogger<JobExecutor> logger
    )
    {
        _scripts = scripts;
        _runs = runs;
        _workspaces = workspaces;
        _workingTree = workingTree;
        _plans = plans;
        _services = services;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Trigger one execution of the script identified by <paramref name="jobScriptId"/>.
    /// Returns the persisted <see cref="JobRun"/> after the run completes
    /// (succeeded, failed, or paused).
    /// </summary>
    public async Task<JobRun> ExecuteAsync(
        Guid jobScriptId,
        IReadOnlyDictionary<string, object?> parameters,
        Guid? triggeredBy,
        string triggerKind,
        CancellationToken ct = default
    )
    {
        var script = await _scripts.FindByIdAsync(jobScriptId, ct);
        if (script is null)
            throw new ArgumentException(
                $"Job script {jobScriptId} not found.",
                nameof(jobScriptId)
            );

        var runId = Guid.NewGuid();
        var startedAt = _time.GetUtcNow().UtcDateTime;
        var totalSw = Stopwatch.StartNew();

        var initialRun = new JobRun(
            Id: runId,
            JobScriptId: script.Id,
            WorkspaceId: script.WorkspaceId,
            Status: JobRunStatus.Running,
            ParametersJson: JsonSerializer.Serialize(parameters),
            StartCommitSha: null,
            EndCommitSha: null,
            StartedAt: startedAt,
            CompletedAt: null,
            TriggeredBy: triggeredBy,
            TriggerKind: triggerKind,
            PredecessorRunId: null,
            PlanId: null,
            FailureMessage: null,
            TotalTokensUsed: null,
            TotalCostUsd: null,
            DurationMs: 0
        );
        await _runs.SaveRunAsync(initialRun, ct);

        try
        {
            var frontmatter = FrontmatterParser.ParseFrontmatter(script.Frontmatter);

            // Resolve the workspace + working tree once per run so the step
            // context can carry the absolute path. Pre-step resolution also
            // surfaces "workspace deleted between job-create and run-trigger"
            // as a clean failure rather than a step-level surprise.
            var workspace = await _workspaces.FindByIdAsync(script.WorkspaceId, ct);
            if (workspace is null)
                throw new InvalidOperationException(
                    $"Workspace {script.WorkspaceId} for job {script.Id} no longer exists."
                );
            var workingTreePath =
                await _workingTree.ResolvePathAsync(workspace, ct) ?? string.Empty;

            // Snapshot the working tree's HEAD before any step runs. Null
            // for non-git workspaces or never-synced git workspaces.
            var startSha = string.IsNullOrEmpty(workingTreePath)
                ? null
                : await _workingTree.ResolveHeadShaAsync(workspace, workingTreePath, ct);

            var normalizedParams = InputsNormalizer.NormalizeRoot(parameters);

            // Three branches:
            // 1. Plan-then-execute — top-level `type: llm-planner` with no
            //    declared `steps:`. The planner step runs first, persists a
            //    JobPlan, then the executor walks the plan.
            // 2. Multi-step DAG — frontmatter declares a `steps:` array.
            // 3. Single-step — legacy shape (top-level type + body).
            RunOutcome outcome;
            Guid? planId = null;
            if (
                frontmatter.Steps.Count == 0
                && string.Equals(frontmatter.Type, "llm-planner", StringComparison.Ordinal)
            )
            {
                (outcome, planId) = await ExecuteLlmPlannerAsync(
                    script,
                    runId,
                    frontmatter,
                    workspace,
                    workingTreePath,
                    normalizedParams,
                    parameters,
                    ct
                );
            }
            else if (frontmatter.Steps.Count > 0)
            {
                outcome = await ExecuteMultiStepAsync(
                    script,
                    runId,
                    frontmatter,
                    workspace,
                    workingTreePath,
                    normalizedParams,
                    ct
                );
            }
            else
            {
                var stepInputs = BuildStepInputs(frontmatter, script.Body, parameters);
                var (singleResult, singleSha) = await ExecuteOneStepAsync(
                    script,
                    runId,
                    position: 0,
                    stepId: Guid.NewGuid(),
                    stepName: script.Name,
                    stepType: frontmatter.Type,
                    declaredAllowedCommands: frontmatter.AllowedCommands,
                    declaredRequiredSecrets: frontmatter.RequiredSecrets,
                    budgets: BuildBudgets(frontmatter),
                    workspace: workspace,
                    workingTreePath: workingTreePath,
                    inputs: stepInputs,
                    ct: ct
                );
                outcome = new RunOutcome(
                    Status: MapToRunStatus(singleResult.Status),
                    LastCommitSha: singleSha,
                    FailureMessage: singleResult.ErrorMessage,
                    TotalTokensUsed: singleResult.TokensUsed,
                    TotalCostUsd: singleResult.CostUsd
                );
            }

            totalSw.Stop();
            var endSha = outcome.LastCommitSha ?? startSha;
            var finalRun = initialRun with
            {
                Status = outcome.Status,
                CompletedAt = _time.GetUtcNow().UtcDateTime,
                StartCommitSha = startSha,
                EndCommitSha = endSha,
                PlanId = planId,
                FailureMessage = outcome.FailureMessage,
                TotalTokensUsed = outcome.TotalTokensUsed,
                TotalCostUsd = outcome.TotalCostUsd,
                DurationMs = totalSw.ElapsedMilliseconds,
            };
            await _runs.SaveRunAsync(finalRun, ct);
            return finalRun;
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            _logger.LogError(ex, "Job run {RunId} failed unexpectedly", runId);
            var failedRun = initialRun with
            {
                Status = JobRunStatus.Failed,
                CompletedAt = _time.GetUtcNow().UtcDateTime,
                FailureMessage = $"{ex.GetType().Name}: {ex.Message}",
                DurationMs = totalSw.ElapsedMilliseconds,
            };
            await _runs.SaveRunAsync(failedRun, ct);
            return failedRun;
        }
    }

    /// <summary>Outcome bundle the run-level finalizer reads to roll up status + commit SHA + totals.</summary>
    private sealed record RunOutcome(
        JobRunStatus Status,
        string? LastCommitSha,
        string? FailureMessage,
        long? TotalTokensUsed,
        decimal? TotalCostUsd
    );

    private static JobRunStatus MapToRunStatus(StepStatus s) =>
        s switch
        {
            StepStatus.Succeeded => JobRunStatus.Succeeded,
            StepStatus.Skipped => JobRunStatus.Succeeded,
            StepStatus.Failed => JobRunStatus.Failed,
            StepStatus.Paused => JobRunStatus.Paused,
            StepStatus.Cancelled => JobRunStatus.Cancelled,
            _ => JobRunStatus.Failed,
        };

    /// <summary>
    /// Walks a multi-step DAG. Validates structure, topologically sorts,
    /// resolves <c>$step_id.field</c> bindings against the accumulator of
    /// upstream outputs, runs each step, propagates cancellation when an
    /// upstream fails, accumulates token/cost totals, and tracks the last
    /// produced commit SHA so the run-level <c>EndCommitSha</c> matches
    /// the final mutation.
    /// </summary>
    private Task<RunOutcome> ExecuteMultiStepAsync(
        JobScript script,
        Guid runId,
        JobScriptFrontmatter frontmatter,
        Workspace workspace,
        string workingTreePath,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct
    ) =>
        ExecuteStepsAsync(
            script,
            runId,
            frontmatter,
            frontmatter.Steps,
            startPosition: 0,
            initialOutputs: new Dictionary<string, IReadOnlyDictionary<string, object?>>(
                StringComparer.Ordinal
            ),
            initialStatuses: new Dictionary<string, StepStatus>(StringComparer.Ordinal),
            initialTotalTokens: null,
            initialTotalCost: null,
            workspace,
            workingTreePath,
            parameters,
            ct
        );

    /// <summary>
    /// Walk a DAG of step declarations. Shared between the multi-step
    /// frontmatter path and the plan-then-execute path. <paramref name="startPosition"/>
    /// + <paramref name="initialOutputs"/> + <paramref name="initialStatuses"/>
    /// let the planner path inject its already-completed planner step at
    /// position 0 so subsequent plan steps can reference <c>$planner.field</c>
    /// bindings without re-running the planner.
    /// </summary>
    private async Task<RunOutcome> ExecuteStepsAsync(
        JobScript script,
        Guid runId,
        JobScriptFrontmatter frontmatter,
        IReadOnlyList<JobScriptStepDecl> steps,
        int startPosition,
        Dictionary<string, IReadOnlyDictionary<string, object?>> initialOutputs,
        Dictionary<string, StepStatus> initialStatuses,
        long? initialTotalTokens,
        decimal? initialTotalCost,
        Workspace workspace,
        string workingTreePath,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct
    )
    {
        var validation = DagValidator.Validate(steps);
        if (validation.Error is not null)
        {
            await PersistDagValidationFailureAsync(script, runId, validation.Error, ct);
            return new RunOutcome(
                Status: JobRunStatus.Failed,
                LastCommitSha: null,
                FailureMessage: validation.Error,
                TotalTokensUsed: initialTotalTokens,
                TotalCostUsd: initialTotalCost
            );
        }

        var stepOutputs = initialOutputs;
        var stepStatuses = initialStatuses;
        var totalTokens = initialTotalTokens;
        var totalCost = initialTotalCost;
        string? lastCommitSha = null;
        var anyFailed = false;
        string? firstFailureMessage = null;

        for (var i = 0; i < validation.Sorted.Count; i++)
        {
            var stepDecl = validation.Sorted[i];
            // Skip steps already marked complete by initial state — supports
            // the planner path where the planner step itself is at the head
            // of the validated DAG but already ran.
            if (stepStatuses.ContainsKey(stepDecl.Id))
                continue;

            var position = startPosition + i;

            var blockedBy = stepDecl.DependsOn.FirstOrDefault(id =>
                stepStatuses.GetValueOrDefault(id) is StepStatus.Failed or StepStatus.Cancelled
            );
            if (blockedBy is not null)
            {
                await PersistCancelledStepAsync(script, runId, stepDecl, position, blockedBy, ct);
                stepStatuses[stepDecl.Id] = StepStatus.Cancelled;
                continue;
            }

            IReadOnlyDictionary<string, object?> resolvedInputs;
            try
            {
                var normalizedDeclaredInputs = InputsNormalizer.NormalizeRoot(stepDecl.Inputs);
                resolvedInputs = StepBindingResolver.Resolve(
                    normalizedDeclaredInputs,
                    stepOutputs,
                    parameters
                );
            }
            catch (StepBindingException ex)
            {
                await PersistBindingFailureAsync(script, runId, stepDecl, position, ex, ct);
                stepStatuses[stepDecl.Id] = StepStatus.Failed;
                anyFailed = true;
                firstFailureMessage ??= ex.Message;
                continue;
            }

            var stepName = string.IsNullOrWhiteSpace(stepDecl.Name) ? stepDecl.Id : stepDecl.Name;
            var (result, commitSha) = await ExecuteOneStepAsync(
                script,
                runId,
                position: position,
                stepId: Guid.NewGuid(),
                stepName: stepName,
                stepType: stepDecl.Type,
                declaredAllowedCommands: frontmatter.AllowedCommands,
                declaredRequiredSecrets: frontmatter.RequiredSecrets,
                budgets: BuildBudgets(frontmatter),
                workspace: workspace,
                workingTreePath: workingTreePath,
                inputs: resolvedInputs,
                ct: ct
            );

            stepStatuses[stepDecl.Id] = result.Status;
            stepOutputs[stepDecl.Id] = result.Outputs;
            if (commitSha is not null)
                lastCommitSha = commitSha;
            if (result.TokensUsed is { } tokens)
                totalTokens = (totalTokens ?? 0) + tokens;
            if (result.CostUsd is { } cost)
                totalCost = (totalCost ?? 0m) + cost;

            if (result.Status is StepStatus.Failed)
            {
                anyFailed = true;
                firstFailureMessage ??= result.ErrorMessage;
            }
        }

        return new RunOutcome(
            Status: anyFailed ? JobRunStatus.Failed : JobRunStatus.Succeeded,
            LastCommitSha: lastCommitSha,
            FailureMessage: firstFailureMessage,
            TotalTokensUsed: totalTokens,
            TotalCostUsd: totalCost
        );
    }

    /// <summary>
    /// Plan-then-execute path. Runs the planner step, fetches the plan it
    /// produced, then walks the plan's steps as a continuation of the
    /// same run. The planner step appears at position 0 in the audit
    /// timeline; plan steps follow at positions 1..N.
    /// </summary>
    private async Task<(RunOutcome Outcome, Guid? PlanId)> ExecuteLlmPlannerAsync(
        JobScript script,
        Guid runId,
        JobScriptFrontmatter frontmatter,
        Workspace workspace,
        string workingTreePath,
        IReadOnlyDictionary<string, object?> normalizedParams,
        IReadOnlyDictionary<string, object?> rawParams,
        CancellationToken ct
    )
    {
        // Run the planner step using the standard single-step input
        // resolution path so body→goal substitution + frontmatter `inputs:`
        // merge work the same way they do for `llm-chat`.
        var plannerInputs = BuildStepInputs(frontmatter, script.Body, rawParams);
        const string PlannerStepId = "planner";
        var (plannerResult, plannerCommit) = await ExecuteOneStepAsync(
            script,
            runId,
            position: 0,
            stepId: Guid.NewGuid(),
            stepName: PlannerStepId,
            stepType: "llm-planner",
            declaredAllowedCommands: frontmatter.AllowedCommands,
            declaredRequiredSecrets: frontmatter.RequiredSecrets,
            budgets: BuildBudgets(frontmatter),
            workspace: workspace,
            workingTreePath: workingTreePath,
            inputs: plannerInputs,
            ct: ct
        );

        if (plannerResult.Status != StepStatus.Succeeded)
        {
            return (
                new RunOutcome(
                    Status: MapToRunStatus(plannerResult.Status),
                    LastCommitSha: plannerCommit,
                    FailureMessage: plannerResult.ErrorMessage,
                    TotalTokensUsed: plannerResult.TokensUsed,
                    TotalCostUsd: plannerResult.CostUsd
                ),
                null
            );
        }

        if (
            !plannerResult.Outputs.TryGetValue("plan_id", out var planIdRaw)
            || planIdRaw is not Guid planId
        )
        {
            return (
                new RunOutcome(
                    Status: JobRunStatus.Failed,
                    LastCommitSha: plannerCommit,
                    FailureMessage: "Planner step succeeded but did not return a `plan_id` output.",
                    TotalTokensUsed: plannerResult.TokensUsed,
                    TotalCostUsd: plannerResult.CostUsd
                ),
                null
            );
        }

        var plan = await _plans.FindByIdAsync(planId, ct);
        if (plan is null)
        {
            return (
                new RunOutcome(
                    Status: JobRunStatus.Failed,
                    LastCommitSha: plannerCommit,
                    FailureMessage: $"Planner step persisted plan {planId} but it could not be loaded for execution.",
                    TotalTokensUsed: plannerResult.TokensUsed,
                    TotalCostUsd: plannerResult.CostUsd
                ),
                null
            );
        }

        // Stamp the actual job script id on the plan now that we know it.
        // The planner runner persisted Guid.Empty because it doesn't have
        // the script id available — backfill so future "list plans by
        // script" queries work.
        if (plan.JobScriptId == Guid.Empty)
            await _plans.SaveAsync(plan with { JobScriptId = script.Id }, ct);

        IReadOnlyList<JobPlanStep> planSteps;
        try
        {
            planSteps =
                JsonSerializer.Deserialize<List<JobPlanStep>>(plan.StepsJson)
                ?? new List<JobPlanStep>();
        }
        catch (Exception ex)
        {
            return (
                new RunOutcome(
                    Status: JobRunStatus.Failed,
                    LastCommitSha: plannerCommit,
                    FailureMessage: $"Failed to deserialize plan {planId} steps: {ex.Message}",
                    TotalTokensUsed: plannerResult.TokensUsed,
                    TotalCostUsd: plannerResult.CostUsd
                ),
                planId
            );
        }

        // Synthesize a list of step decls including the planner at the head
        // (so cross-references via $planner.field bind correctly) plus the
        // plan's own steps. Plan steps that don't declare a depends_on
        // become dependent on the planner implicitly so they don't sort
        // ahead of it.
        var synthesizedSteps = new List<JobScriptStepDecl>(planSteps.Count + 1)
        {
            new()
            {
                Id = PlannerStepId,
                Name = PlannerStepId,
                Type = "llm-planner",
                DependsOn = new List<string>(),
                Inputs = new Dictionary<string, object?>(),
            },
        };
        foreach (var ps in planSteps)
        {
            var deps = ps.DependsOn.ToList();
            if (deps.Count == 0)
                deps.Add(PlannerStepId);
            synthesizedSteps.Add(
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

        var initialOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(
            StringComparer.Ordinal
        )
        {
            [PlannerStepId] = plannerResult.Outputs,
        };
        var initialStatuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            [PlannerStepId] = StepStatus.Succeeded,
        };

        var outcome = await ExecuteStepsAsync(
            script,
            runId,
            frontmatter,
            synthesizedSteps,
            startPosition: 0,
            initialOutputs: initialOutputs,
            initialStatuses: initialStatuses,
            initialTotalTokens: plannerResult.TokensUsed,
            initialTotalCost: plannerResult.CostUsd,
            workspace,
            workingTreePath,
            normalizedParams,
            ct
        );

        // The planner step already produced a commit sha (probably null —
        // planner is read-only) and we want the LAST plan-step's commit
        // to be the run-level EndCommitSha.
        if (outcome.LastCommitSha is null && plannerCommit is not null)
            outcome = outcome with { LastCommitSha = plannerCommit };

        return (outcome, planId);
    }

    /// <summary>
    /// Run a single step with the supplied identity + budgets. Used by
    /// both the single-step and multi-step paths so the audit shape +
    /// apply-and-commit semantics are identical.
    /// </summary>
    private async Task<(StepResult Result, string? CommitSha)> ExecuteOneStepAsync(
        JobScript script,
        Guid runId,
        int position,
        Guid stepId,
        string stepName,
        string stepType,
        IReadOnlyList<string> declaredAllowedCommands,
        IReadOnlyList<string> declaredRequiredSecrets,
        StepBudgets budgets,
        Workspace workspace,
        string workingTreePath,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var stepStartedAt = _time.GetUtcNow().UtcDateTime;
        var inputsJson = JsonSerializer.Serialize(inputs);
        var inputsHash = Sha256(inputsJson);
        var idempotencyKey = Sha256(stepName + "|" + stepType + "|" + inputsHash);

        // Persist the step record up-front so audit shows "running" even if
        // the host crashes during execution.
        var stepRecord = new JobRunStep(
            Id: stepId,
            RunId: runId,
            Position: position,
            StepType: stepType,
            Name: stepName,
            Status: StepStatus.Running,
            IdempotencyKey: idempotencyKey,
            CachedFromStepId: null,
            InputsJson: inputsJson,
            OutputsJson: null,
            InputsHash: inputsHash,
            FileChangeCount: 0,
            CommitSha: null,
            StartedAt: stepStartedAt,
            CompletedAt: null,
            DurationMs: 0,
            TokensUsed: null,
            CostUsd: null,
            ErrorMessage: null,
            ResumeToken: null
        );
        await _runs.SaveStepAsync(stepRecord, ct);

        var runner = _services.GetKeyedService<IStepRunner>(stepType);
        if (runner is null)
        {
            var failed = stepRecord with
            {
                Status = StepStatus.Failed,
                ErrorMessage =
                    $"Unknown step type '{stepType}'. No registered IStepRunner with that key.",
                CompletedAt = _time.GetUtcNow().UtcDateTime,
            };
            await _runs.SaveStepAsync(failed, ct);
            return (StepResult.Failure(failed.ErrorMessage!, 0), null);
        }

        var allowedCommands =
            declaredAllowedCommands.Count == 0
                ? null
                : (IReadOnlySet<string>)
                    new HashSet<string>(declaredAllowedCommands, StringComparer.Ordinal);
        var requiredSecrets =
            declaredRequiredSecrets.Count == 0
                ? null
                : (IReadOnlySet<string>)
                    new HashSet<string>(declaredRequiredSecrets, StringComparer.Ordinal);

        var ctx = new StepContext(
            RunId: runId,
            WorkspaceId: workspace.Id,
            WorkspaceSlug: workspace.Slug,
            WorkingTreePath: workingTreePath,
            StepId: stepId,
            StepName: stepName,
            Budgets: budgets,
            Logger: _services.GetRequiredService<ILoggerFactory>().CreateLogger(stepType),
            AllowedCommands: allowedCommands,
            RequiredSecrets: requiredSecrets,
            ResumeToken: null
        );

        StepResult result;
        try
        {
            result = await runner.ExecuteAsync(ctx, inputs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Step {StepType} threw during execution", stepType);
            result = StepResult.Failure($"{ex.GetType().Name}: {ex.Message}", 0);
        }

        // Apply + commit any file changes the step produced. The
        // architectural rule (architecture.md "File mutation discipline"):
        // each step that emits FileChange[] gets its own commit on git
        // workspaces, with the SHA recorded on the step record.
        //
        // Failures from ApplyAndCommitAsync are surfaced on the step's
        // ErrorMessage rather than thrown — the runner already succeeded;
        // the failure is at the persistence boundary. Step status flips to
        // Failed so the run rolls up correctly.
        string? commitSha = null;
        var applyError = (string?)null;
        if (result.Status == StepStatus.Succeeded && result.FileChanges.Count > 0)
        {
            try
            {
                var commitMessage = BuildCommitMessage(stepName, runId, stepId, result);
                var apply = await _workingTree.ApplyAndCommitAsync(
                    workspace,
                    workingTreePath,
                    result.FileChanges,
                    commitMessage,
                    ct
                );
                commitSha = apply.CommitSha;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "ApplyAndCommitAsync failed for step {StepId} of run {RunId}",
                    stepId,
                    runId
                );
                applyError =
                    $"Step ran but applying changes to the working tree failed: {ex.Message}";
                result = result with { Status = StepStatus.Failed, ErrorMessage = applyError };
            }
        }

        var completed = stepRecord with
        {
            Status = result.Status,
            OutputsJson = JsonSerializer.Serialize(result.Outputs),
            FileChangeCount = result.FileChanges.Count,
            CommitSha = commitSha,
            CompletedAt = _time.GetUtcNow().UtcDateTime,
            DurationMs = result.DurationMs,
            TokensUsed = result.TokensUsed,
            CostUsd = result.CostUsd,
            ErrorMessage = result.ErrorMessage,
            ResumeToken = result.ResumeToken,
        };
        await _runs.SaveStepAsync(completed, ct);
        return (result, commitSha);
    }

    /// <summary>
    /// Persist a synthetic step record carrying the DAG-validation error so
    /// the audit timeline shows where the run died. The record sits at
    /// position 0 with a marker step type so the UI can render it
    /// distinctly.
    /// </summary>
    private async Task PersistDagValidationFailureAsync(
        JobScript script,
        Guid runId,
        string error,
        CancellationToken ct
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _runs.SaveStepAsync(
            new JobRunStep(
                Id: Guid.NewGuid(),
                RunId: runId,
                Position: 0,
                StepType: "_dag_validation",
                Name: "DAG validation",
                Status: StepStatus.Failed,
                IdempotencyKey: Sha256("_dag_validation|" + script.Id),
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

    /// <summary>
    /// Persist a Cancelled step record when an upstream step failed. Captures
    /// which upstream blocked it so the audit UI can render the dependency
    /// chain.
    /// </summary>
    private async Task PersistCancelledStepAsync(
        JobScript script,
        Guid runId,
        JobScriptStepDecl stepDecl,
        int position,
        string blockedBy,
        CancellationToken ct
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _runs.SaveStepAsync(
            new JobRunStep(
                Id: Guid.NewGuid(),
                RunId: runId,
                Position: position,
                StepType: stepDecl.Type,
                Name: string.IsNullOrWhiteSpace(stepDecl.Name) ? stepDecl.Id : stepDecl.Name,
                Status: StepStatus.Cancelled,
                IdempotencyKey: Sha256("cancelled|" + script.Id + "|" + stepDecl.Id),
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
                ErrorMessage: $"Cancelled — upstream step '{blockedBy}' did not succeed.",
                ResumeToken: null
            ),
            ct
        );
    }

    /// <summary>
    /// Persist a failed step record when binding resolution can't satisfy
    /// the step's inputs (typo in $step.field, missing parameter, etc.).
    /// </summary>
    private async Task PersistBindingFailureAsync(
        JobScript script,
        Guid runId,
        JobScriptStepDecl stepDecl,
        int position,
        StepBindingException ex,
        CancellationToken ct
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _runs.SaveStepAsync(
            new JobRunStep(
                Id: Guid.NewGuid(),
                RunId: runId,
                Position: position,
                StepType: stepDecl.Type,
                Name: string.IsNullOrWhiteSpace(stepDecl.Name) ? stepDecl.Id : stepDecl.Name,
                Status: StepStatus.Failed,
                IdempotencyKey: Sha256("binding|" + script.Id + "|" + stepDecl.Id),
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
                ErrorMessage: ex.Message,
                ResumeToken: null
            ),
            ct
        );
    }

    private static string BuildCommitMessage(
        string stepName,
        Guid runId,
        Guid stepId,
        StepResult result
    )
    {
        // Structured commit message per architecture.md "Commit batching":
        //   [creuser] <step.name> (run=<run_id> step=<step_id>)
        //   ...
        var sb = new StringBuilder();
        sb.Append("[creuser] ").Append(stepName).Append(" (run=").Append(runId.ToString("N")[..8]);
        sb.Append(" step=").Append(stepId.ToString("N")[..8]).Append(')');

        if (result.FileChanges.Count > 0)
        {
            sb.Append('\n').Append('\n');
            sb.Append("Changed:");
            foreach (var c in result.FileChanges.Take(20))
            {
                sb.Append('\n').Append("- ");
                switch (c.Op)
                {
                    case FileChangeOp.Create:
                        sb.Append("create ").Append(c.Path);
                        break;
                    case FileChangeOp.Modify:
                        sb.Append("modify ").Append(c.Path);
                        break;
                    case FileChangeOp.Delete:
                        sb.Append("delete ").Append(c.Path);
                        break;
                    case FileChangeOp.Rename:
                        sb.Append("rename ").Append(c.Path).Append(" -> ").Append(c.RenameTo);
                        break;
                }
            }
            if (result.FileChanges.Count > 20)
                sb.Append('\n')
                    .Append("- … and ")
                    .Append(result.FileChanges.Count - 20)
                    .Append(" more");
        }
        return sb.ToString();
    }

    private static StepBudgets BuildBudgets(JobScriptFrontmatter frontmatter)
    {
        var b = frontmatter.Budgets;
        if (b is null)
            return new StepBudgets();
        return new StepBudgets(
            MaxDuration: b.MaxDurationSeconds.HasValue
                ? TimeSpan.FromSeconds(b.MaxDurationSeconds.Value)
                : null,
            MaxTokens: b.MaxTokens,
            MaxCostUsd: b.MaxCostUsd
        );
    }

    private static IReadOnlyDictionary<string, object?> BuildStepInputs(
        JobScriptFrontmatter frontmatter,
        string body,
        IReadOnlyDictionary<string, object?> parameters
    )
    {
        // For v0.1: merge frontmatter `inputs:` with the per-run parameters,
        // and inject the body into the runner's expected slot. Each step type
        // has its own conventions:
        //
        //   llm-chat → body becomes `prompt`
        //   shell    → body becomes `script`        (later)
        //   csharp   → body becomes `script`        (later)
        //
        // Multi-step DAGs land in a subsequent slice; for now this mapping is
        // hardcoded by the executor. Step bindings ($step_id.output_name) are
        // not yet supported.
        var merged = new Dictionary<string, object?>();
        foreach (var kv in frontmatter.Inputs)
            merged[kv.Key] = kv.Value;
        foreach (var kv in parameters)
            merged[kv.Key] = kv.Value;

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

        // Normalize the heterogeneous shapes we get from YAML (object-keyed
        // dicts) and JSON (JsonElement-leaved trees) into a canonical
        // string-keyed shape with native primitives. Runners then read inputs
        // with one set of casts.
        return InputsNormalizer.NormalizeRoot(merged);
    }

    private static string Sha256(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
