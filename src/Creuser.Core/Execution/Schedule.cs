namespace Creuser.Core.Execution;

/// <summary>
/// One configured trigger that fires a job. Two kinds in v0.1:
/// <list type="bullet">
///   <item><see cref="ScheduleKind.Cron"/> — fires on the cadence of a NCrontab-parseable expression. UTC-evaluated; per-job time zones are post-v1.</item>
///   <item><see cref="ScheduleKind.Sync"/> — fires after every successful workspace sync. Cron expression is null.</item>
/// </list>
///
/// <para>
/// Multiple schedules can target the same job (e.g. a daily cron + a
/// post-sync trigger as two rows). Each row carries its own enabled flag
/// and last-fired bookkeeping so disabling one trigger doesn't affect the
/// others.
/// </para>
/// </summary>
public sealed record Schedule(
    Guid Id,
    Guid WorkspaceId,
    Guid JobScriptId,
    /// <summary>One of <see cref="ScheduleKind.Cron"/> or <see cref="ScheduleKind.Sync"/>.</summary>
    string Kind,
    /// <summary>Required when <see cref="Kind"/> is cron; null otherwise. NCrontab expression in 5- or 6-field form.</summary>
    string? CronExpression,
    bool Enabled,
    /// <summary>For cron schedules: when this schedule next fires. Recomputed after each firing. Null for sync schedules and disabled rows.</summary>
    DateTime? NextDueAt,
    /// <summary>UTC of last firing — succeeded or failed.</summary>
    DateTime? LastFiredAt,
    /// <summary>Run id produced by the most recent firing.</summary>
    Guid? LastRunId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy
);

/// <summary>The two trigger sources v0.1 supports. Webhook + git-push triggers land later.</summary>
public static class ScheduleKind
{
    public const string Cron = "cron";
    public const string Sync = "sync";

    public static bool IsValid(string kind) => kind is Cron or Sync;
}

public interface IScheduleStore
{
    Task<Schedule?> FindByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>All schedules in a workspace, regardless of kind / enabled state. UI listing.</summary>
    Task<IReadOnlyList<Schedule>> ListByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default
    );

    /// <summary>Schedules for a specific job. Used by the per-job UI panel.</summary>
    Task<IReadOnlyList<Schedule>> ListByJobAsync(Guid jobScriptId, CancellationToken ct = default);

    /// <summary>
    /// Cron schedules whose <see cref="Schedule.NextDueAt"/> has elapsed
    /// and which are enabled. Used by the scheduler tick.
    /// </summary>
    Task<IReadOnlyList<Schedule>> ListDueCronAsync(DateTime asOf, CancellationToken ct = default);

    /// <summary>
    /// Sync schedules for a workspace. Fired by the sync handler after a
    /// successful sync.
    /// </summary>
    Task<IReadOnlyList<Schedule>> ListSyncTriggeredAsync(
        Guid workspaceId,
        CancellationToken ct = default
    );

    Task SaveAsync(Schedule schedule, CancellationToken ct = default);

    /// <summary>Update the bookkeeping after a firing — <see cref="Schedule.LastFiredAt"/>, <see cref="Schedule.LastRunId"/>, and (for cron) the new <see cref="Schedule.NextDueAt"/>.</summary>
    Task MarkFiredAsync(
        Guid scheduleId,
        DateTime firedAt,
        DateTime? nextDueAt,
        Guid? runId,
        CancellationToken ct = default
    );

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
