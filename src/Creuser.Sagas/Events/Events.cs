namespace Creuser.Sagas.Events;

/// <summary>
/// Marten event-store records. Each <c>JobRun</c> is a stream keyed by
/// <c>RunId</c>; events are appended on every state transition. The
/// stream is the authoritative history; <c>cr.job_runs</c> + <c>cr.job_run_steps</c>
/// remain the operational read-model populated by the saga handlers
/// directly (custom storage projection — see
/// <c>docs/wip/wolverine-marten-design.md</c> "Persistence and projections").
///
/// <para>
/// v1 events focus on the run lifecycle; finer-grained events (per-file
/// change, per-LLM-call, per-tool-invocation) are post-v1 work — they
/// enable richer replay scenarios but aren't needed for the core
/// audit + durability story.
/// </para>
/// </summary>
public sealed record JobRunStarted(
    Guid RunId,
    Guid JobScriptId,
    Guid WorkspaceId,
    string TriggerKind,
    Guid? TriggeredBy,
    string ParametersJson,
    string? StartCommitSha,
    DateTime StartedAt
);

public sealed record JobRunStepStarted(
    Guid StepId,
    Guid RunId,
    int Position,
    string StepType,
    string StepDeclId,
    string Name,
    string IdempotencyKey,
    string InputsJson,
    DateTime StartedAt
);

public sealed record JobRunStepCompleted(
    Guid StepId,
    Guid RunId,
    string StepDeclId,
    string OutputsJson,
    int FileChangeCount,
    string? CommitSha,
    long? TokensUsed,
    decimal? CostUsd,
    long DurationMs,
    DateTime CompletedAt
);

public sealed record JobRunStepFailed(
    Guid StepId,
    Guid RunId,
    string StepDeclId,
    string ErrorMessage,
    long DurationMs,
    DateTime FailedAt
);

public sealed record JobRunStepCancelled(
    Guid StepId,
    Guid RunId,
    string StepDeclId,
    string BlockedByStepId,
    DateTime CancelledAt
);

public sealed record JobRunCompleted(
    Guid RunId,
    /// <summary>One of <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>.</summary>
    string Status,
    string? FailureMessage,
    string? EndCommitSha,
    Guid? PlanId,
    long? TotalTokensUsed,
    decimal? TotalCostUsd,
    long DurationMs,
    DateTime CompletedAt
);

public sealed record JobPlanLoaded(Guid RunId, Guid PlanId, int StepCount, DateTime LoadedAt);
