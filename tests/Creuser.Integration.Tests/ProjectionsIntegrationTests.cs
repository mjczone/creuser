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

    [Fact]
    public async Task GetEntityRelationships_RendersOutgoingFolderFromConventionRule()
    {
        // Trigger sync to populate the projection.
        var sync = await _client.PostAsync(
            $"/api/workspaces/{_workspaceSlug}/projections/sync",
            content: null
        );
        sync.EnsureSuccessStatusCode();

        // logout has one resolved `implements` edge → login.
        var resp = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/entities/business_rule/logout/relationships"
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("logout", result.GetProperty("slug").GetString());
        Assert.Equal("business_rule", result.GetProperty("kind").GetString());

        var folders = result.GetProperty("folders").EnumerateArray().ToList();
        Assert.Single(folders);
        var folder = folders[0];
        Assert.Equal("implements", folder.GetProperty("kind").GetString());
        Assert.Equal("Implements", folder.GetProperty("name").GetString());
        Assert.Equal("out", folder.GetProperty("direction").GetString());

        var items = folder.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("business_rule", items[0].GetProperty("kind").GetString());
        Assert.Equal("login", items[0].GetProperty("slug").GetString());
        Assert.Equal("entity", items[0].GetProperty("metadataKind").GetString());
    }

    [Fact]
    public async Task GetEntityRelationships_UnresolvedRefsRenderAsFileOrSlug()
    {
        var sync = await _client.PostAsync(
            $"/api/workspaces/{_workspaceSlug}/projections/sync",
            content: null
        );
        sync.EnsureSuccessStatusCode();

        // refund has one unresolved `implements` → "missing-rule" (slug shape, not a path).
        var resp = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/entities/business_rule/refund/relationships"
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var folders = doc
            .RootElement.GetProperty("result")
            .GetProperty("folders")
            .EnumerateArray()
            .ToList();
        Assert.Single(folders);
        var item = folders[0].GetProperty("items").EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null, item.GetProperty("entityId").ValueKind);
        // The legacy `select_frontmatter` resolves as Slug → metadata.kind = "slug" when unresolved.
        Assert.Equal("slug", item.GetProperty("metadataKind").GetString());
        Assert.Equal("missing-rule", item.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task GetCapabilities_ReturnsSchemaAndAccessorRegistry()
    {
        var resp = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/conventions/capabilities"
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var caps = doc.RootElement.GetProperty("result");

        Assert.Equal("v1", caps.GetProperty("schemaVersion").GetString());

        // workspaceKinds should include the seeded business_rule.
        var kinds = caps.GetProperty("workspaceKinds")
            .EnumerateArray()
            .Select(k => k.GetString())
            .ToList();
        Assert.Contains("business_rule", kinds);

        // Schema embedded; accessors enumerated; common patterns surfaced.
        Assert.True(caps.GetProperty("schema").TryGetProperty("$id", out _));
        Assert.NotEmpty(caps.GetProperty("accessors").EnumerateArray());
        Assert.NotEmpty(caps.GetProperty("commonPatterns").EnumerateArray());
        Assert.Contains(
            caps.GetProperty("interpretModes").EnumerateArray().Select(e => e.GetString()),
            v => v == "auto"
        );
    }

    [Fact]
    public async Task ValidateConvention_RejectsMalformedYaml()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/conventions/validate",
            new { yaml = "this is: : not a convention: : :" }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var v = doc.RootElement.GetProperty("result");
        Assert.False(v.GetProperty("isValid").GetBoolean());
        Assert.NotEmpty(v.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task TestConvention_MatchedFile_ReturnsEntityAndRefs()
    {
        // Sync first so relationships have peers to resolve against.
        var sync = await _client.PostAsync(
            $"/api/workspaces/{_workspaceSlug}/projections/sync",
            content: null
        );
        sync.EnsureSuccessStatusCode();

        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/conventions/business_rule/test",
            new { againstPath = "business-rules/auth/logout.md" }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var t = doc.RootElement.GetProperty("result");
        Assert.True(t.GetProperty("matched").GetBoolean());
        Assert.Equal("logout", t.GetProperty("entity").GetProperty("slug").GetString());
        Assert.Equal(1, t.GetProperty("refs").GetArrayLength());
        Assert.Equal(
            "implements",
            t.GetProperty("refs")[0].GetProperty("relationship").GetString()
        );
    }

    [Fact]
    public async Task AddRelationship_AppendsRuleAndPersistsToDisk()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/conventions/business_rule/relationships",
            new
            {
                kind = "related",
                name = "Related",
                source = "frontmatter.related",
                interpret = "auto",
                targetKind = "any",
                inverse = "related",
            }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(2, result.GetProperty("relationshipCount").GetInt32());
        Assert.Contains("kind: related", result.GetProperty("resultingYaml").GetString());

        // The convention file on disk now declares two rules.
        var written = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, ".creuser", "conventions", "business-rule.yaml")
        );
        Assert.Contains("kind: related", written);
        Assert.Contains("kind: implements", written);
    }

    [Fact]
    public async Task RemoveRelationship_DeletesRuleFromYaml()
    {
        var resp = await _client.DeleteAsync(
            $"/api/workspaces/{_workspaceSlug}/conventions/business_rule/relationships/implements"
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(0, result.GetProperty("relationshipCount").GetInt32());
        Assert.DoesNotContain("kind: implements", result.GetProperty("resultingYaml").GetString()!);
    }

    [Fact]
    public async Task AddRelationship_DuplicateKind_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/conventions/business_rule/relationships",
            new
            {
                kind = "implements",
                source = "frontmatter.implements",
                interpret = "slug",
                targetKind = "business_rule",
            }
        );
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetEntityRelationships_EntityMissing_Returns404()
    {
        var resp = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/entities/business_rule/nonexistent/relationships"
        );
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
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
