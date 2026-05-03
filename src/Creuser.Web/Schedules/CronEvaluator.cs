using NCrontab;

namespace Creuser.Web.Schedules;

/// <summary>
/// Wraps NCrontab's parser with a try-parse pattern + a single-spot for
/// computing next-due times. All cron expressions are evaluated in UTC —
/// per-job time zones are deferred to a later pass when multi-tenant
/// deployments need them. Single-tenant on-prem operators set their cron
/// expressions assuming UTC and document the offset internally.
/// </summary>
public static class CronEvaluator
{
    /// <summary>
    /// Parse a cron expression. Accepts standard 5-field expressions
    /// (<c>m h dom mon dow</c>); 6-field (with seconds) is also supported
    /// via NCrontab's <c>IncludingSeconds</c> option, detected by counting
    /// fields.
    /// </summary>
    public static bool TryParse(string? expression, out CrontabSchedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression))
            return false;
        try
        {
            var fieldCount = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var options = new CrontabSchedule.ParseOptions { IncludingSeconds = fieldCount == 6 };
            schedule = CrontabSchedule.Parse(expression, options);
            return true;
        }
        catch (CrontabException)
        {
            return false;
        }
    }

    /// <summary>Compute the next firing time strictly after <paramref name="afterUtc"/>. Returns null on parse failure.</summary>
    public static DateTime? ComputeNextDue(string? expression, DateTime afterUtc)
    {
        if (!TryParse(expression, out var schedule) || schedule is null)
            return null;
        // GetNextOccurrence is exclusive of `afterUtc`, so the same-instant
        // case correctly emits the following occurrence.
        return schedule.GetNextOccurrence(afterUtc);
    }
}
