using Creuser.Core.Execution;

namespace Creuser.Sagas.Commands;

/// <summary>
/// External entry-point command — published by the API endpoint, the cron
/// tick, the sync hook, and the manual-fire flow. Wolverine routes this to
/// <c>JobRunSaga.Start</c>.
/// </summary>
public sealed record StartJobRun(
    Guid RunId,
    Guid JobScriptId,
    IReadOnlyDictionary<string, object?> Parameters,
    Guid? TriggeredBy,
    string TriggerKind
);

/// <summary>
/// Internal — saga publishes this to dispatch one step. Routed to
/// <see cref="Handlers.StepDispatchHandler"/> which resolves the
/// <c>IStepRunner</c> from DI and invokes it.
/// </summary>
public sealed record DispatchStep(
    Guid RunId,
    Guid StepId,
    string StepDeclId,
    int Position,
    string StepType,
    string StepName,
    Guid WorkspaceId,
    string WorkspaceSlug,
    string WorkingTreePath,
    IReadOnlyDictionary<string, object?> Inputs,
    IReadOnlyList<string>? AllowedCommands,
    IReadOnlyList<string>? RequiredSecrets,
    long? BudgetMaxDurationSeconds,
    long? BudgetMaxTokens,
    decimal? BudgetMaxCostUsd
);

/// <summary>
/// Internal — dispatch handler publishes after a step succeeds. Routed
/// back to the saga which advances state.
/// </summary>
public sealed record StepCompleted(
    Guid RunId,
    Guid StepId,
    string StepDeclId,
    string OutputsJson,
    int FileChangeCount,
    string? CommitSha,
    long? TokensUsed,
    decimal? CostUsd,
    long DurationMs
);

/// <summary>
/// Internal — dispatch handler publishes when a step fails. Routed back
/// to the saga which propagates cancellation to dependents.
/// </summary>
public sealed record StepFailed(
    Guid RunId,
    Guid StepId,
    string StepDeclId,
    string ErrorMessage,
    long DurationMs
);

/// <summary>
/// Internal — saga publishes when an upstream failure cascades a downstream
/// step into Cancelled status. Currently a record-only event because the
/// saga writes the persistence directly; kept as a typed message for
/// future fan-out (notifications, replay observability).
/// </summary>
public sealed record StepCancelled(
    Guid RunId,
    Guid StepId,
    string StepDeclId,
    string BlockedByStepId
);

/// <summary>
/// External — published from the post-v1 pause/resume infrastructure when
/// a paused step's wake-up condition is satisfied. Routes to the saga
/// which re-publishes <see cref="DispatchStep"/> with the resume token in
/// scope. v1 ships the type; the wake-up sources (timer, webhook, manual)
/// land later.
/// </summary>
public sealed record ResumeStep(Guid RunId, Guid StepId, string ResumeToken);

/// <summary>
/// External — published to abort an in-flight run. Saga handler marks the
/// run cancelled, persists, and the dispatch handler honors a cancelled
/// flag on the next step it picks up. v1 ships the type; full
/// cancellation propagation through long-running step runners is a
/// follow-up.
/// </summary>
public sealed record CancelRun(Guid RunId, string Reason);

/// <summary>
/// Future — replay an existing run with one of the cache / soft / hard
/// flavours documented in architecture.md "Auditability and replay". v1
/// ships the command type; the handler stub returns a "not implemented"
/// failure. Wiring up cache / soft / hard semantics is strictly additive
/// once the rails are in place.
/// </summary>
public sealed record ReplayJobRun(Guid PriorRunId, string ReplayMode, Guid? TriggeredBy);

/// <summary>
/// Returned by the API endpoint helper to indicate the saga has finished
/// (succeeded, failed, or cancelled). Kept as a Wolverine-compatible
/// record so the synchronous endpoint path can await via the
/// <c>RunCompletionWaiter</c> service.
/// </summary>
public sealed record JobRunFinished(Guid RunId, JobRunStatus Status, string? FailureMessage);
