using Creuser.Core.Execution;
using Microsoft.Extensions.Hosting;

namespace Creuser.Web.Schedules;

/// <summary>
/// Background service that ticks every <see cref="TickInterval"/> (30s by
/// default), looks up cron schedules whose <c>next_due_at</c> has elapsed,
/// and dispatches them via <see cref="IJobScheduleDispatcher"/>. Failures
/// log + continue — a busted schedule shouldn't take down the tick loop.
///
/// <para>
/// The 30s interval is chosen to balance cron precision against database
/// load: at one tick per 30s, schedules fire within ~30s of their declared
/// time, and the only DB hit per tick is a single indexed query against
/// <c>cr.schedules WHERE enabled AND kind='cron' AND next_due_at &lt;= now</c>.
/// Sub-minute cron expressions still work — they just may catch up
/// multiple firings in one tick if the host was paused.
/// </para>
///
/// <para>
/// Multi-instance deployments will need a Postgres advisory lock around
/// the tick to prevent the same schedule firing from two hosts at once.
/// Single-tenant on-prem v0.1 is fine without.
/// </para>
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    /// <summary>Default tick interval. Test override via <see cref="WithInterval"/> or env var <c>CREUSER_SCHEDULER_INTERVAL_MS</c>.</summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<SchedulerService> _logger;
    private readonly TimeSpan _interval;

    public TimeSpan TickInterval => _interval;

    public SchedulerService(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<SchedulerService> logger,
        IConfiguration config
    )
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;

        // Allow tests + ops to tighten the interval (e.g. integration tests
        // setting it to 100ms so a scheduled run lands within seconds).
        var configured = config["CREUSER_SCHEDULER_INTERVAL_MS"];
        _interval =
            int.TryParse(configured, out var ms) && ms > 0
                ? TimeSpan.FromMilliseconds(ms)
                : DefaultTickInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SchedulerService started; tick interval = {Interval}", _interval);
        // Wait one interval before the first tick so app startup completes
        // (DbInitializer, hosted services). Otherwise the first tick races
        // against schema creation.
        try
        {
            await Task.Delay(_interval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SchedulerService tick failed");
            }
            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // Snapshot the due list inside a scope so the connection lifetime
        // is bounded; dispatch each schedule in its own scope so one
        // long-running job doesn't pin the others.
        IReadOnlyList<Schedule> due;
        using (var scope = _scopeFactory.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
            due = await store.ListDueCronAsync(now, ct);
        }
        if (due.Count == 0)
            return;

        foreach (var schedule in due)
        {
            // Fire-and-forget. The dispatcher updates bookkeeping (last
            // fired + next due) so subsequent ticks don't double-fire.
            // Capture local to avoid closure-over-loop-var.
            var s = schedule;
            _ = Task.Run(
                async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dispatcher =
                        scope.ServiceProvider.GetRequiredService<IJobScheduleDispatcher>();
                    await dispatcher.DispatchAsync(s, ScheduleKind.Cron, CancellationToken.None);
                },
                CancellationToken.None
            );
        }
    }
}
