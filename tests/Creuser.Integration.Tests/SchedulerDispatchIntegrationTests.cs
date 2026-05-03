using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// Auto-fire integration coverage for the scheduler. Two paths the tick
/// + sync hook take that <see cref="SchedulesApiTests"/> doesn't exercise:
/// <list type="number">
///   <item><see cref="SchedulerService"/> waking up, querying due cron
///   schedules, and dispatching a job without any HTTP caller.</item>
///   <item><see cref="WorkspacesEndpoints"/>.Sync detecting a sync-kind
///   schedule and dispatching it as a side-effect of a successful sync.</item>
/// </list>
/// Tests run with a tightened scheduler interval (200ms) so cron firings
/// land inside test wall-time. The 2-second cron expression
/// <c>*/2 * * * * *</c> guarantees a next-due that the next tick will
/// pick up without depending on real-clock minutes.
/// </summary>
public sealed class SchedulerDispatchIntegrationTests
    : IClassFixture<PostgresFixture>,
        IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;
    private Guid _jobId;

    public SchedulerDispatchIntegrationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _factory = new CreuserApiFactory
        {
            ConnectionString = _pg.ConnectionString,
            // Tight tick so cron schedules fire within a few seconds rather
            // than the production 30s default.
            SchedulerIntervalMs = 200,
        };
        _client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await Login("admin@creuser.test", "ChangeMe!");

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-sched-disp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        _workspaceSlug = $"sd-{Guid.NewGuid():N}"[..16];

        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Scheduler Dispatch Workspace",
                description = "fixture",
                type = "local",
                localSettings = new { path = _workspacePath, writable = true },
            }
        );
        createWs.EnsureSuccessStatusCode();

        // Stub job — file-mutate with no ops is the cheapest no-op runner.
        var jobResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "stub",
                name = "Stub Job",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: file-mutate\ninputs:\n  ops: []\n",
                body = "",
                status = "active",
            }
        );
        jobResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await jobResp.Content.ReadAsStreamAsync());
        _jobId = doc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        try
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; tests should not fail on a leftover temp dir.
        }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task SchedulerTick_FiresDueCronSchedule_PopulatesLastFiredAndCreatesRun()
    {
        // 6-field expression — every 2 seconds. Combined with the 200ms
        // tick, a fresh schedule's next_due_at lands within ~2s and the
        // tick after that picks it up.
        var scheduleId = await CreateCronScheduleAsync("*/2 * * * * *");

        var fired = await PollUntilLastFiredAsync(scheduleId, TimeSpan.FromSeconds(15));
        Assert.True(
            fired is not null,
            "Scheduler tick did not fire the cron schedule within 15 seconds."
        );

        // The run record itself must be visible on the workspace, attributed
        // to the cron trigger so the timeline UI can render the cause.
        var runs = await ListRunsAsync();
        Assert.NotEmpty(runs);
        Assert.Contains(
            runs,
            r =>
                r.GetProperty("triggerKind").GetString() == "cron"
                && r.GetProperty("jobScriptId").GetGuid() == _jobId
        );

        // After firing, next_due_at must be re-armed (the dispatcher
        // recomputes it post-run); otherwise the schedule would be stuck.
        var s = await GetScheduleAsync(scheduleId);
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("nextDueAt").ValueKind);
    }

    [Fact]
    public async Task SyncHook_FiresSyncTriggeredSchedule_PopulatesLastFiredAndCreatesRun()
    {
        // Sync-kind schedules ignore the cron tick — they fire as a side
        // effect of a successful workspace sync. Local sync always succeeds
        // when the path is accessible.
        var scheduleId = await CreateSyncScheduleAsync();

        var sync = await _client.PostAsync($"/api/workspaces/{_workspaceSlug}/sync", null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        var fired = await PollUntilLastFiredAsync(scheduleId, TimeSpan.FromSeconds(10));
        Assert.True(
            fired is not null,
            "Sync hook did not fire the sync-triggered schedule within 10 seconds."
        );

        var runs = await ListRunsAsync();
        Assert.Contains(
            runs,
            r =>
                r.GetProperty("triggerKind").GetString() == "sync"
                && r.GetProperty("jobScriptId").GetGuid() == _jobId
        );

        // Sync schedules must NOT acquire a next_due_at — they aren't
        // tick-driven and a populated value would cause the cron query
        // to surface them.
        var s = await GetScheduleAsync(scheduleId);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("nextDueAt").ValueKind);
    }

    [Fact]
    public async Task SchedulerTick_DisabledCronSchedule_DoesNotFire()
    {
        // Sanity check on the negative path — a disabled schedule must
        // not be picked up by the tick even when the same 2-second cron
        // would otherwise be due many times over the wait window.
        var scheduleId = await CreateCronScheduleAsync("*/2 * * * * *", enabled: false);

        // Wait long enough that an enabled schedule would have fired ~3x.
        await Task.Delay(TimeSpan.FromSeconds(6));

        var s = await GetScheduleAsync(scheduleId);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("lastFiredAt").ValueKind);
    }

    private async Task<Guid> CreateCronScheduleAsync(string expression, bool enabled = true)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/",
            new
            {
                jobScriptId = _jobId,
                kind = "cron",
                cronExpression = expression,
                enabled,
            }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("result").GetProperty("scheduleId").GetGuid();
    }

    private async Task<Guid> CreateSyncScheduleAsync()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/",
            new
            {
                jobScriptId = _jobId,
                kind = "sync",
                cronExpression = (string?)null,
                enabled = true,
            }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("result").GetProperty("scheduleId").GetGuid();
    }

    private async Task<DateTime?> PollUntilLastFiredAsync(Guid scheduleId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var s = await GetScheduleAsync(scheduleId);
            if (s.GetProperty("lastFiredAt").ValueKind != JsonValueKind.Null)
                return s.GetProperty("lastFiredAt").GetDateTime();
            await Task.Delay(200);
        }
        return null;
    }

    private async Task<JsonElement> GetScheduleAsync(Guid scheduleId)
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/schedules/");
        resp.EnsureSuccessStatusCode();
        // Clone so the returned element survives the JsonDocument's disposal.
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        foreach (var s in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            if (s.GetProperty("scheduleId").GetGuid() == scheduleId)
                return s.Clone();
        }
        throw new InvalidOperationException($"Schedule {scheduleId} not found in list.");
    }

    private async Task<List<JsonElement>> ListRunsAsync()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/");
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc
            .RootElement.GetProperty("result")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
