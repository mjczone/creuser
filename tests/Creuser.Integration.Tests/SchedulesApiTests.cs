using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// CRUD + manual-fire tests for the schedules surface. Validates auth,
/// validation, lookup, and the manual `fire` endpoint that bypasses the
/// scheduler tick. Auto-firing via cron + sync-hook propagation are
/// covered separately in <c>SchedulerDispatchIntegrationTests</c>.
/// </summary>
public sealed class SchedulesApiTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private Guid _jobId;

    public SchedulesApiTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _factory = new CreuserApiFactory { ConnectionString = _pg.ConnectionString };
        _client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await Login("admin@creuser.test", "ChangeMe!");

        var workspacePath = Path.Combine(
            Path.GetTempPath(),
            $"creuser-sched-api-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(workspacePath);
        _workspaceSlug = $"sa-{Guid.NewGuid():N}"[..16];

        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Schedules API Workspace",
                description = "fixture",
                type = "local",
                localSettings = new { path = workspacePath, writable = true },
            }
        );
        createWs.EnsureSuccessStatusCode();

        // Create a stub job (file-mutate, no-op when ops list is empty).
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
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task ListSchedules_Empty_ReturnsEmpty()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/schedules/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task CreateCron_ValidExpression_PersistsWithNextDueComputed()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/",
            new
            {
                jobScriptId = _jobId,
                kind = "cron",
                cronExpression = "0 6 * * *",
                enabled = true,
            }
        );
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var s = doc.RootElement.GetProperty("result");
        Assert.Equal("cron", s.GetProperty("kind").GetString());
        Assert.Equal("0 6 * * *", s.GetProperty("cronExpression").GetString());
        Assert.True(s.GetProperty("enabled").GetBoolean());
        // next_due_at must be populated for an enabled cron schedule.
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("nextDueAt").ValueKind);
    }

    [Fact]
    public async Task CreateCron_InvalidExpression_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/",
            new
            {
                jobScriptId = _jobId,
                kind = "cron",
                cronExpression = "not a cron",
                enabled = true,
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateCron_EmptyExpression_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/",
            new
            {
                jobScriptId = _jobId,
                kind = "cron",
                cronExpression = (string?)null,
                enabled = true,
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateSync_NoCronExpression_Persists()
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
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var s = doc.RootElement.GetProperty("result");
        Assert.Equal("sync", s.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("nextDueAt").ValueKind);
    }

    [Fact]
    public async Task CreateSync_WithCronExpression_Returns400()
    {
        // Sync schedules forbid cron expressions — operators picking
        // either/or shouldn't accidentally combine them.
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/",
            new
            {
                jobScriptId = _jobId,
                kind = "sync",
                cronExpression = "0 6 * * *",
                enabled = true,
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateSchedule_DisablingClearsNextDue()
    {
        var scheduleId = await CreateCronSchedule("*/5 * * * *");

        var update = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/{scheduleId}",
            new
            {
                kind = "cron",
                cronExpression = "*/5 * * * *",
                enabled = false,
            }
        );
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await update.Content.ReadAsStreamAsync());
        var s = doc.RootElement.GetProperty("result");
        Assert.False(s.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("nextDueAt").ValueKind);
    }

    [Fact]
    public async Task DeleteSchedule_RemovesFromList()
    {
        var scheduleId = await CreateCronSchedule("0 6 * * *");
        var del = await _client.DeleteAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/{scheduleId}"
        );
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var list = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/schedules/");
        using var doc = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task FireSchedule_Manual_ReturnsRunIdAndUpdatesLastFiredAt()
    {
        var scheduleId = await CreateCronSchedule("0 6 * * *");

        var fire = await _client.PostAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/{scheduleId}/fire",
            content: null
        );
        Assert.Equal(HttpStatusCode.OK, fire.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await fire.Content.ReadAsStreamAsync());
        var runId = doc.RootElement.GetProperty("result").GetGuid();
        Assert.NotEqual(Guid.Empty, runId);

        // The schedule's last_fired_at must now be populated.
        var list = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/schedules/");
        using var listDoc = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync());
        var s = listDoc.RootElement.GetProperty("result")[0];
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("lastFiredAt").ValueKind);
        Assert.Equal(runId, s.GetProperty("lastRunId").GetGuid());
    }

    [Fact]
    public async Task NonAdmin_CannotAccessSchedules()
    {
        var create = await _client.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                email = "u@s.example.com",
                displayName = "U",
                role = "User",
                temporaryPassword = "TempPass99",
            }
        );
        create.EnsureSuccessStatusCode();
        await _client.PostAsync("/api/auth/logout", null);
        await Login("u@s.example.com", "TempPass99");

        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/schedules/");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task<Guid> CreateCronSchedule(string expression)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/schedules/",
            new
            {
                jobScriptId = _jobId,
                kind = "cron",
                cronExpression = expression,
                enabled = true,
            }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("result").GetProperty("scheduleId").GetGuid();
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
