using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end projection layer test: creates a local workspace with a
/// <c>.creuser/conventions/</c> directory, drops a few files matching a
/// convention, triggers a projection sync via the API, and asserts the
/// entities are queryable through both the entities-list endpoint and
/// the get-by-kind/slug endpoint.
/// </summary>
public sealed class ProjectionsIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public ProjectionsIntegrationTests(PostgresFixture pg)
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

        _workspacePath = Path.Combine(
            Path.GetTempPath(),
            $"creuser-projections-int-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_workspacePath);

        // Seed a convention + a few matching files.
        var conventionsDir = Path.Combine(_workspacePath, ".creuser", "conventions");
        Directory.CreateDirectory(conventionsDir);
        await File.WriteAllTextAsync(
            Path.Combine(conventionsDir, "business-rule.yaml"),
            """
            id: business_rule
            description: Markdown business rules.
            priority: 100
            match:
              glob: "business-rules/**/*.md"
              exclude:
                - "business-rules/**/index.md"
            slug:
              from: filename
              transform: kebab
            metadata:
              source: frontmatter
              required:
                - title
                - owner
            relationships:
              - kind: implements
                select_frontmatter: implements
                target_kind: business_rule
            """
        );

        await SeedFile("business-rules/auth/login.md", "---\ntitle: Login\nowner: alice\n---\n");
        await SeedFile(
            "business-rules/auth/logout.md",
            "---\ntitle: Logout\nowner: bob\nimplements:\n  - login\n---\n"
        );
        await SeedFile(
            "business-rules/billing/refund.md",
            "---\ntitle: Refund\nowner: carol\nimplements:\n  - missing-rule\n---\n"
        );

        _workspaceSlug = $"prj-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Projections Test",
                description = "fixture",
                type = "local",
                localSettings = new { path = _workspacePath, writable = true },
            }
        );
        createWs.EnsureSuccessStatusCode();
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
            // best effort
        }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task SyncProjection_PopulatesEntitiesAndRefs()
    {
        var sync = await _client.PostAsync(
            $"/api/workspaces/{_workspaceSlug}/projections/sync",
            content: null
        );
        sync.EnsureSuccessStatusCode();
        using var syncDoc = await JsonDocument.ParseAsync(await sync.Content.ReadAsStreamAsync());
        var report = syncDoc.RootElement.GetProperty("result").GetProperty("report");
        Assert.Equal(3, report.GetProperty("entityTotal").GetInt32());
        Assert.Equal(1, report.GetProperty("refsResolved").GetInt32());
        Assert.Equal(1, report.GetProperty("refsUnresolved").GetInt32());

        // Listing entities returns the three projected rows.
        var list = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/entities/");
        list.EnsureSuccessStatusCode();
        using var listDoc = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync());
        var entities = listDoc.RootElement.GetProperty("result");
        Assert.Equal(3, entities.GetArrayLength());

        // Filter by kind via query string.
        var kindList = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/entities/?kind=business_rule"
        );
        using var kindDoc = await JsonDocument.ParseAsync(
            await kindList.Content.ReadAsStreamAsync()
        );
        Assert.Equal(3, kindDoc.RootElement.GetProperty("result").GetArrayLength());

        // Fetch a single entity — refs should include the resolved + unresolved edges.
        var get = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/entities/business_rule/logout"
        );
        get.EnsureSuccessStatusCode();
        using var getDoc = await JsonDocument.ParseAsync(await get.Content.ReadAsStreamAsync());
        var detail = getDoc.RootElement.GetProperty("result");
        Assert.Equal("logout", detail.GetProperty("slug").GetString());
        var refsOut = detail.GetProperty("refsOut").EnumerateArray().ToList();
        Assert.Single(refsOut);
        Assert.Equal("implements", refsOut[0].GetProperty("relationship").GetString());
        Assert.Equal("login", refsOut[0].GetProperty("targetSlug").GetString());
        Assert.True(refsOut[0].GetProperty("toEntityId").ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task ListConventions_ReturnsLoadedConvention()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/conventions/");
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var conventions = doc.RootElement.GetProperty("result").GetProperty("conventions");
        Assert.Equal(1, conventions.GetArrayLength());
        Assert.Equal("business_rule", conventions[0].GetProperty("id").GetString());
        Assert.Equal("business-rules/**/*.md", conventions[0].GetProperty("glob").GetString());
    }

    [Fact]
    public async Task ProjectionSyncStepRunner_RunsAsJob()
    {
        // Trigger a `type: projection-sync` job and assert the run records
        // the expected step output (entities_total = 3).
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "rebuild-projection",
                name = "rebuild projection",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: projection-sync\n",
                body = "",
                status = "active",
            }
        );
        resp.EnsureSuccessStatusCode();
        using var jobDoc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var jobId = jobDoc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var runDoc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = runDoc.RootElement.GetProperty("result");
        Assert.Equal("succeeded", result.GetProperty("status").GetString());

        var runId = result.GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        Assert.Equal("projection-sync", step.GetProperty("stepType").GetString());
        var outputsJson = step.GetProperty("outputsJson").GetString()!;
        using var outputsDoc = JsonDocument.Parse(outputsJson);
        Assert.Equal(3, outputsDoc.RootElement.GetProperty("entities_total").GetInt32());
    }

    private async Task SeedFile(string relativePath, string content)
    {
        var full = Path.Combine(_workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
