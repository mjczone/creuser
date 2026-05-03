using Creuser.Web.Schedules;

namespace Creuser.Integration.Tests;

/// <summary>
/// Pure unit tests for the cron parser wrapper. No fixture, no DB —
/// they sit in the integration project because <see cref="CronEvaluator"/>
/// lives in Creuser.Web alongside the SchedulerService.
/// </summary>
public class CronEvaluatorTests
{
    [Theory]
    [InlineData("* * * * *", true)] // every minute
    [InlineData("0 * * * *", true)] // top of every hour
    [InlineData("0 6 * * *", true)] // 06:00 daily
    [InlineData("*/5 * * * *", true)] // every 5 min
    [InlineData("0 0 * * 0", true)] // midnight Sunday
    [InlineData("0 0 1 * *", true)] // first of month
    [InlineData("0 9-17 * * 1-5", true)] // weekdays during work hours
    [InlineData("0 0 0 0 0", false)] // 0 isn't a valid month
    [InlineData("not a cron", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_RecognizedExpressions(string? expr, bool expectedOk)
    {
        var ok = CronEvaluator.TryParse(expr, out var schedule);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedOk, schedule is not null);
    }

    [Fact]
    public void TryParse_SixFieldExpression_AcceptedWithSeconds()
    {
        // NCrontab six-field form supports per-second granularity.
        var ok = CronEvaluator.TryParse("0 */5 * * * *", out var schedule);
        Assert.True(ok);
        Assert.NotNull(schedule);
    }

    [Fact]
    public void ComputeNextDue_DailyAt6_ReturnsTomorrowAt6_WhenAfterToday6()
    {
        // Anchored UTC reference time: 2026-05-02 12:00:00 UTC. Daily 6am
        // schedule's next firing is 2026-05-03 06:00:00 UTC.
        var anchor = new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc);
        var next = CronEvaluator.ComputeNextDue("0 6 * * *", anchor);
        Assert.Equal(new DateTime(2026, 5, 3, 6, 0, 0), next);
    }

    [Fact]
    public void ComputeNextDue_DailyAt6_ReturnsTodayAt6_WhenBeforeToday6()
    {
        // Same reference but before 6am. Next firing is today.
        var anchor = new DateTime(2026, 5, 2, 3, 0, 0, DateTimeKind.Utc);
        var next = CronEvaluator.ComputeNextDue("0 6 * * *", anchor);
        Assert.Equal(new DateTime(2026, 5, 2, 6, 0, 0), next);
    }

    [Fact]
    public void ComputeNextDue_AtBoundary_ExclusiveOfAnchor()
    {
        // GetNextOccurrence is exclusive: when the anchor IS a firing
        // time, the next-due skips ahead.
        var anchor = new DateTime(2026, 5, 2, 6, 0, 0, DateTimeKind.Utc);
        var next = CronEvaluator.ComputeNextDue("0 6 * * *", anchor);
        Assert.Equal(new DateTime(2026, 5, 3, 6, 0, 0), next);
    }

    [Fact]
    public void ComputeNextDue_InvalidExpression_ReturnsNull()
    {
        var next = CronEvaluator.ComputeNextDue("nope", DateTime.UtcNow);
        Assert.Null(next);
    }
}
