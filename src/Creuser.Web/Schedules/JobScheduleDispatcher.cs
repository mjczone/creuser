using Creuser.Core.Execution;
using Creuser.Scripting;

namespace Creuser.Web.Schedules;

/// <summary>
/// Default <see cref="IJobScheduleDispatcher"/> — fires the schedule's job
/// in a freshly-created DI scope so the calling scope (a sync request, a
/// scheduler tick) doesn't pin the executor's lifetime. Updates the
/// schedule's bookkeeping after the run completes.
/// </summary>
public sealed class JobScheduleDispatcher : IJobScheduleDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<JobScheduleDispatcher> _logger;

    public JobScheduleDispatcher(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<JobScheduleDispatcher> logger
    )
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    public async Task<Guid?> DispatchAsync(
        Schedule schedule,
        string triggerKind,
        CancellationToken ct = default
    )
    {
        using var scope = _scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<JobExecutor>();
        var scheduleStore = scope.ServiceProvider.GetRequiredService<IScheduleStore>();

        Guid? runId = null;
        try
        {
            var run = await executor.ExecuteAsync(
                schedule.JobScriptId,
                new Dictionary<string, object?>(),
                triggeredBy: null,
                triggerKind: triggerKind,
                ct: ct
            );
            runId = run.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Schedule {ScheduleId} ({TriggerKind}) for job {JobId} failed during dispatch",
                schedule.Id,
                triggerKind,
                schedule.JobScriptId
            );
        }

        // Bookkeeping update happens whether the run succeeded or failed.
        // For cron schedules: compute the next due time so the tick won't
        // immediately re-fire. For sync schedules: next_due_at stays null.
        var firedAt = _time.GetUtcNow().UtcDateTime;
        var nextDue =
            schedule.Kind == ScheduleKind.Cron
                ? CronEvaluator.ComputeNextDue(schedule.CronExpression, firedAt)
                : null;
        try
        {
            await scheduleStore.MarkFiredAsync(schedule.Id, firedAt, nextDue, runId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to update schedule {ScheduleId} bookkeeping after dispatch",
                schedule.Id
            );
        }

        return runId;
    }
}
