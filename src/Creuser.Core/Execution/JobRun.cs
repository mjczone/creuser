namespace Creuser.Core.Execution;

/// <summary>
/// One execution of a <see cref="JobScript"/>. Carries the audit trail —
/// status, parameters, per-step records, commit SHA bracket, token /
/// cost totals — and is what the SPA's RunInspector renders.
///
/// <para>
/// Stored in <c>cr.job_runs</c> with one row per run; per-step records live
/// in <c>cr.job_run_steps</c> (<see cref="JobRunStep"/>). The executor
/// inserts a <see cref="JobRun"/> + N <see cref="JobRunStep"/> rows in the
/// same DB transaction and updates them as steps complete.
/// </para>
/// </summary>
public sealed record JobRun(
    Guid Id,
    Guid JobScriptId,
    Guid WorkspaceId,
    JobRunStatus Status,
    /// <summary>Parameters supplied at trigger time, merged with the script's defaults.</summary>
    string ParametersJson,
    /// <summary>Working tree SHA at run start. Null for local-type workspaces (no git history).</summary>
    string? StartCommitSha,
    /// <summary>Working tree SHA after the run's final commit. Equals <see cref="StartCommitSha"/> if the run made no mutations.</summary>
    string? EndCommitSha,
    DateTime StartedAt,
    DateTime? CompletedAt,
    Guid? TriggeredBy,
    /// <summary>One of <c>manual</c>, <c>cron</c>, <c>sync</c>, <c>api</c>.</summary>
    string TriggerKind,
    /// <summary>Run that this one resumed from (if pause + resume), or replayed from (if replay).</summary>
    Guid? PredecessorRunId,
    /// <summary>For plan-then-execute runs: the persisted plan id.</summary>
    Guid? PlanId,
    string? FailureMessage,
    long? TotalTokensUsed,
    decimal? TotalCostUsd,
    long DurationMs
);

/// <summary>
/// One step's record within a run. The structural unit of audit + replay.
///
/// <para>
/// <see cref="IdempotencyKey"/> is what lets the executor skip re-execution
/// when an identical step from a prior successful run on the same workspace
/// hashes to the same key — see architecture.md "Idempotency and caching".
/// On a skip, <see cref="CachedFromStepId"/> points at the original
/// <see cref="JobRunStep"/> whose outputs this row inherits.
/// </para>
/// </summary>
public sealed record JobRunStep(
    Guid Id,
    Guid RunId,
    int Position,
    string StepType,
    string Name,
    StepStatus Status,
    string IdempotencyKey,
    Guid? CachedFromStepId,
    /// <summary>Resolved inputs JSON (after binding upstream outputs and defaults).</summary>
    string InputsJson,
    /// <summary>Outputs JSON. Null until the step completes.</summary>
    string? OutputsJson,
    /// <summary>Sha256 of the inputs canonicalization, for indexed cache lookups.</summary>
    string InputsHash,
    /// <summary>Number of file mutations the step applied. Detail rows live in <c>cr.job_run_step_changes</c> when needed; the count is here for UI summary.</summary>
    int FileChangeCount,
    /// <summary>Commit SHA produced by THIS step's mutations, when any. Null when the step made no file changes.</summary>
    string? CommitSha,
    DateTime StartedAt,
    DateTime? CompletedAt,
    long DurationMs,
    long? TokensUsed,
    decimal? CostUsd,
    string? ErrorMessage,
    string? ResumeToken
);

public interface IJobRunStore
{
    Task<JobRun?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<JobRun>> ListByWorkspaceAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<JobRun>> ListByScriptAsync(
        Guid scriptId,
        int skip,
        int take,
        CancellationToken ct = default
    );
    Task SaveRunAsync(JobRun run, CancellationToken ct = default);
    Task SaveStepAsync(JobRunStep step, CancellationToken ct = default);
    Task<IReadOnlyList<JobRunStep>> ListStepsAsync(Guid runId, CancellationToken ct = default);
}
