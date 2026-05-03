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
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<JobExecutor> _logger;

    public JobExecutor(
        IJobScriptStore scripts,
        IJobRunStore runs,
        IWorkspaceStore workspaces,
        IWorkspaceWorkingTree workingTree,
        IServiceProvider services,
        TimeProvider time,
        ILogger<JobExecutor> logger
    )
    {
        _scripts = scripts;
        _runs = runs;
        _workspaces = workspaces;
        _workingTree = workingTree;
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
            // v0.1: parse frontmatter, treat as single-step job. The body of
            // the script becomes the step's `prompt` input for llm-chat
            // types; for other types the binding rules will differ.
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

            var stepInputs = BuildStepInputs(frontmatter, script.Body, parameters);
            var (stepResult, stepCommitSha) = await ExecuteSingleStepAsync(
                script,
                runId,
                frontmatter,
                workspace,
                workingTreePath,
                stepInputs,
                ct
            );

            totalSw.Stop();
            var status = stepResult.Status switch
            {
                StepStatus.Succeeded => JobRunStatus.Succeeded,
                StepStatus.Skipped => JobRunStatus.Succeeded,
                StepStatus.Failed => JobRunStatus.Failed,
                StepStatus.Paused => JobRunStatus.Paused,
                _ => JobRunStatus.Failed,
            };

            // EndCommitSha = the SHA produced by the last step's commit, or
            // StartCommitSha if no step produced changes (git no-op runs).
            // Null for non-git workspaces.
            var endSha = stepCommitSha ?? startSha;

            var finalRun = initialRun with
            {
                Status = status,
                CompletedAt = _time.GetUtcNow().UtcDateTime,
                StartCommitSha = startSha,
                EndCommitSha = endSha,
                FailureMessage = stepResult.ErrorMessage,
                TotalTokensUsed = stepResult.TokensUsed,
                TotalCostUsd = stepResult.CostUsd,
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

    private async Task<(StepResult Result, string? CommitSha)> ExecuteSingleStepAsync(
        JobScript script,
        Guid runId,
        JobScriptFrontmatter frontmatter,
        Workspace workspace,
        string workingTreePath,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var stepType = frontmatter.Type;
        var stepId = Guid.NewGuid();
        var stepStartedAt = _time.GetUtcNow().UtcDateTime;
        var inputsJson = JsonSerializer.Serialize(inputs);
        var inputsHash = Sha256(inputsJson);
        var idempotencyKey = Sha256(stepType + "|" + inputsHash);

        // Persist the step record up-front so audit shows "running" even if
        // the host crashes during execution.
        var stepRecord = new JobRunStep(
            Id: stepId,
            RunId: runId,
            Position: 0,
            StepType: stepType,
            Name: script.Name,
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
            frontmatter.AllowedCommands.Count == 0
                ? null
                : (IReadOnlySet<string>)
                    new HashSet<string>(frontmatter.AllowedCommands, StringComparer.Ordinal);
        var requiredSecrets =
            frontmatter.RequiredSecrets.Count == 0
                ? null
                : (IReadOnlySet<string>)
                    new HashSet<string>(frontmatter.RequiredSecrets, StringComparer.Ordinal);

        var ctx = new StepContext(
            RunId: runId,
            WorkspaceId: workspace.Id,
            WorkspaceSlug: workspace.Slug,
            WorkingTreePath: workingTreePath,
            StepId: stepId,
            StepName: script.Name,
            Budgets: BuildBudgets(frontmatter),
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
                var commitMessage = BuildCommitMessage(script, runId, stepId, result);
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

    private static string BuildCommitMessage(
        JobScript script,
        Guid runId,
        Guid stepId,
        StepResult result
    )
    {
        // Structured commit message per architecture.md "Commit batching":
        //   [creuser] <step.name> (run=<run_id> step=<step_id>)
        //   ...
        var sb = new StringBuilder();
        sb.Append("[creuser] ")
            .Append(script.Name)
            .Append(" (run=")
            .Append(runId.ToString("N")[..8]);
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
