using Creuser.Core.Execution;

namespace Creuser.Web.Schedules;

/// <summary>
/// Dispatches a schedule firing — runs the job in a fresh DI scope (so a
/// long-running job doesn't pin the calling scope's lifetime) and updates
/// the schedule's bookkeeping (last-fired-at, next-due-at, last-run-id).
/// Used by both the cron tick (<see cref="SchedulerService"/>) and the
/// sync hook in <c>WorkspacesEndpoints.Sync</c>.
///
/// <para>
/// Returns the produced run id. Callers that want fire-and-forget
/// semantics simply discard the returned task; callers that need to
/// observe (tests, future blocking-trigger UI) await it. The dispatcher
/// itself never throws — failures land on the run record's
/// <c>FailureMessage</c> like any other failed run.
/// </para>
/// </summary>
public interface IJobScheduleDispatcher
{
    Task<Guid?> DispatchAsync(
        Schedule schedule,
        string triggerKind,
        CancellationToken ct = default
    );
}
