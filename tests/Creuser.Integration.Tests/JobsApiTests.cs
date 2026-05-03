using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end tests for the Jobs surface — workspace-scoped CRUD over
/// <c>/api/workspaces/{slug}/jobs</c>. Run execution itself isn't covered
/// here because it would require a real LLM provider; the surface that's
/// exercised is the persistence + validation + auth path.
/// </summary>
public sealed class JobsApiTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;

    public JobsApiTests(PostgresFixture pg)
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

        // Bootstrap admin login + create a workspace each test can attach to.
        await Login("admin@creuser.test", "ChangeMe!");
        _workspaceSlug = $"jobs-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Jobs Test Workspace",
                description = "fixture",
                type = "local",
                localSettings = new { path = Path.GetTempPath(), writable = true },
            }
        );
        createWs.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task ListJobs_EmptyWorkspace_ReturnsEmptyList()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/jobs/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public async Task CreateJob_ValidPayload_PersistsAndReturnsRecord()
    {
        var payload = new
        {
            slug = "haiku-generator",
            name = "Haiku generator",
            description = "Demo single-step llm-chat job",
            pattern = "deterministic",
            frontmatter = "type: llm-chat\n",
            body = "Write a haiku about reproducible builds.",
            status = "active",
        };

        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            payload
        );
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("haiku-generator", result.GetProperty("slug").GetString());
        Assert.Equal("Haiku generator", result.GetProperty("name").GetString());
        Assert.Equal("active", result.GetProperty("status").GetString());

        // List should now contain it.
        var list = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/jobs/");
        using var listDoc = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync());
        var arr = listDoc.RootElement.GetProperty("result");
        Assert.Equal(1, arr.GetArrayLength());
    }

    [Fact]
    public async Task CreateJob_DuplicateSlug_Returns409Conflict()
    {
        var payload = new
        {
            slug = "duplicate-slug",
            name = "First",
            description = (string?)null,
            pattern = "deterministic",
            frontmatter = "type: llm-chat\n",
            body = "first body",
            status = "draft",
        };
        await _client.PostAsJsonAsync($"/api/workspaces/{_workspaceSlug}/jobs/", payload);

        var second = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            payload with
            {
                name = "Second",
            }
        );
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("job-script-slug-already-exists", body);
    }

    [Fact]
    public async Task CreateJob_InvalidPattern_Returns400ValidationFailed()
    {
        var payload = new
        {
            slug = "bad-pattern-job",
            name = "Bad pattern",
            description = (string?)null,
            pattern = "purple-haze", // not a valid pattern
            frontmatter = "type: llm-chat\n",
            body = "irrelevant",
            status = "active",
        };

        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            payload
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateJob_ChangesPersistAndUpdatedAtAdvances()
    {
        var jobId = await CreateJobAsync(slug: "updatable-job", name: "Original");
        await Task.Delay(15); // ensure updated_at strictly increases under fast Postgres clock

        var updateResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}",
            new
            {
                name = "Renamed",
                description = "now with a description",
                pattern = "deterministic",
                frontmatter = "type: llm-chat\n",
                body = "new body content",
                status = "disabled",
            }
        );
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await updateResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("Renamed", result.GetProperty("name").GetString());
        Assert.Equal("disabled", result.GetProperty("status").GetString());
        Assert.Equal("now with a description", result.GetProperty("description").GetString());
    }

    [Fact]
    public async Task DeleteJob_RemovesFromList()
    {
        var jobId = await CreateJobAsync(slug: "ephemeral-job", name: "To delete");

        var del = await _client.DeleteAsync($"/api/workspaces/{_workspaceSlug}/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var get = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task ListRunsByWorkspace_EmptyWhenNothingHasRun()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task GetJob_UnknownId_Returns404()
    {
        var resp = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{Guid.NewGuid()}"
        );
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("job-script-not-found", body);
    }

    [Fact]
    public async Task NonAdmin_CannotAccessJobsEndpoints()
    {
        // Create + login as a regular user.
        var create = await _client.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                email = "user@jobs.example.com",
                displayName = "Jobs User",
                role = "User",
                temporaryPassword = "TempPass99",
            }
        );
        create.EnsureSuccessStatusCode();
        await _client.PostAsync("/api/auth/logout", null);
        await Login("user@jobs.example.com", "TempPass99");

        var listAttempt = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/jobs/");
        Assert.Equal(HttpStatusCode.Forbidden, listAttempt.StatusCode);
    }

    [Fact]
    public async Task CreateJob_OnUnknownWorkspace_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/workspaces/no-such-workspace/jobs/",
            new
            {
                slug = "x",
                name = "x",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "",
                body = "",
                status = "draft",
            }
        );
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("workspace-not-found", await resp.Content.ReadAsStringAsync());
    }

    private async Task<Guid> CreateJobAsync(string slug, string name)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug,
                name,
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: llm-chat\n",
                body = "body",
                status = "active",
            }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
