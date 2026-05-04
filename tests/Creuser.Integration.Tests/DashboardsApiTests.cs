using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Creuser.Integration.Tests;

/// <summary>
/// CRUD + nav-tree round-trip for dashboards and dashboard groups, plus
/// the default-seeding behavior triggered by workspace creation. Asserts:
/// the seeder runs (Home + Operations group + Runs/Scripts/Schedules child
/// dashboards appear), default rows are protected from hard-delete, the
/// nav-tree shape matches the SPA's expectations.
/// </summary>
public sealed class DashboardsApiTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public DashboardsApiTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _factory = new CreuserApiFactory { ConnectionString = _pg.ConnectionString };
        _client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await Login("admin@creuser.test", "ChangeMe!");

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-dash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        _workspaceSlug = $"dsh-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Dashboard Test",
                description = "fixture",
                type = "local",
                localSettings = new { path = _workspacePath, writable = true },
            }
        );
        createWs.EnsureSuccessStatusCode();

        // Seeder runs as a fire-and-forget continuation. Wait briefly for
        // it to land before assertions; the seed is small and runs in
        // milliseconds, but we give it generous slack on a busy CI box.
        await WaitForSeed();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        try
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
        catch { }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task SeederPopulates_HomeAndOperationsGroup()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/dashboards/");
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var tree = doc.RootElement.GetProperty("result");

        var standalones = tree.GetProperty("standalones");
        Assert.True(standalones.GetArrayLength() >= 1);
        var home = standalones
            .EnumerateArray()
            .FirstOrDefault(d => d.GetProperty("slug").GetString() == "home");
        Assert.Equal("home", home.GetProperty("slug").GetString());
        Assert.Equal("Home", home.GetProperty("name").GetString());
        Assert.Equal("home", home.GetProperty("icon").GetString());

        var groups = tree.GetProperty("groups");
        var ops = groups
            .EnumerateArray()
            .FirstOrDefault(g => g.GetProperty("slug").GetString() == "operations");
        Assert.Equal("operations", ops.GetProperty("slug").GetString());
        Assert.Equal("Operations", ops.GetProperty("name").GetString());

        var children = ops.GetProperty("children");
        var childSlugs = children
            .EnumerateArray()
            .Select(c => c.GetProperty("slug").GetString())
            .OrderBy(s => s)
            .ToList();
        Assert.Equal(new[] { "runs", "schedules", "scripts" }, childSlugs);
    }

    [Fact]
    public async Task GetDashboard_ReturnsLayoutAndWidgets()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/dashboards/home");
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var dash = doc.RootElement.GetProperty("result");
        Assert.Equal("home", dash.GetProperty("slug").GetString());
        Assert.True(dash.GetProperty("isDefault").GetBoolean());

        var layoutJson = dash.GetProperty("layoutJson").GetString()!;
        var widgetsJson = dash.GetProperty("widgetsJson").GetString()!;
        using var layoutDoc = JsonDocument.Parse(layoutJson);
        using var widgetsDoc = JsonDocument.Parse(widgetsJson);
        Assert.Equal(JsonValueKind.Object, layoutDoc.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Array, widgetsDoc.RootElement.ValueKind);
        Assert.True(widgetsDoc.RootElement.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task DeleteDefaultDashboard_ReturnsValidationError()
    {
        var resp = await _client.DeleteAsync($"/api/workspaces/{_workspaceSlug}/dashboards/home");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateUpdateDashboard_RoundTrip()
    {
        var createResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboards/",
            new
            {
                slug = "my-board",
                name = "My Board",
                icon = "bar_chart",
                position = 50,
            }
        );
        createResp.EnsureSuccessStatusCode();
        using var createDoc = await JsonDocument.ParseAsync(
            await createResp.Content.ReadAsStreamAsync()
        );
        var created = createDoc.RootElement.GetProperty("result");
        Assert.Equal("my-board", created.GetProperty("slug").GetString());
        Assert.False(created.GetProperty("isDefault").GetBoolean());

        // Update with a layout + widgets payload.
        var newLayout = "{\"grid\":{\"root\":{\"type\":\"leaf\"}}}";
        var newWidgets = "[{\"id\":\"w-x1\",\"widgetType\":\"RunsList\",\"props\":{\"limit\":5}}]";
        var updateResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboards/my-board",
            new { layoutJson = newLayout, widgetsJson = newWidgets }
        );
        updateResp.EnsureSuccessStatusCode();

        var afterResp = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboards/my-board"
        );
        using var afterDoc = await JsonDocument.ParseAsync(
            await afterResp.Content.ReadAsStreamAsync()
        );
        var after = afterDoc.RootElement.GetProperty("result");
        // Postgres JSONB strips whitespace + reorders keys; compare semantic
        // shape (specific values we care about) rather than raw strings.
        var parsedLayout = JsonNode.Parse(after.GetProperty("layoutJson").GetString()!)!;
        Assert.Equal("leaf", parsedLayout["grid"]!["root"]!["type"]!.GetValue<string>());
        var parsedWidgets = JsonNode
            .Parse(after.GetProperty("widgetsJson").GetString()!)!
            .AsArray();
        Assert.Single(parsedWidgets);
        Assert.Equal("w-x1", parsedWidgets[0]!["id"]!.GetValue<string>());
        Assert.Equal("RunsList", parsedWidgets[0]!["widgetType"]!.GetValue<string>());
        Assert.Equal(5, parsedWidgets[0]!["props"]!["limit"]!.GetValue<int>());

        // User-created dashboards CAN be deleted.
        var delResp = await _client.DeleteAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboards/my-board"
        );
        delResp.EnsureSuccessStatusCode();
        var checkResp = await _client.GetAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboards/my-board"
        );
        Assert.Equal(HttpStatusCode.NotFound, checkResp.StatusCode);
    }

    [Fact]
    public async Task UpdateDashboard_RejectsMalformedJson()
    {
        var resp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboards/home",
            new { layoutJson = "{ this is not json", widgetsJson = "[]" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GroupCrud_RoundTripAndDeleteOrphans()
    {
        // Create group.
        var createResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboard-groups/",
            new
            {
                slug = "analytics",
                name = "Analytics",
                icon = "analytics",
                position = 20,
            }
        );
        createResp.EnsureSuccessStatusCode();

        // Add a dashboard inside.
        var dashResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboards/",
            new
            {
                slug = "kpi-board",
                name = "KPI Board",
                groupSlug = "analytics",
                position = 0,
            }
        );
        dashResp.EnsureSuccessStatusCode();

        // Confirm nav tree includes the group + child.
        var navBefore = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/dashboards/");
        using var beforeDoc = await JsonDocument.ParseAsync(
            await navBefore.Content.ReadAsStreamAsync()
        );
        var grpBefore = beforeDoc
            .RootElement.GetProperty("result")
            .GetProperty("groups")
            .EnumerateArray()
            .First(g => g.GetProperty("slug").GetString() == "analytics");
        Assert.Equal(1, grpBefore.GetProperty("children").GetArrayLength());

        // Delete the group; the child should orphan to standalone.
        var delResp = await _client.DeleteAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboard-groups/analytics"
        );
        delResp.EnsureSuccessStatusCode();

        var navAfter = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/dashboards/");
        using var afterDoc = await JsonDocument.ParseAsync(
            await navAfter.Content.ReadAsStreamAsync()
        );
        var grpAfter = afterDoc
            .RootElement.GetProperty("result")
            .GetProperty("groups")
            .EnumerateArray()
            .FirstOrDefault(g => g.GetProperty("slug").GetString() == "analytics");
        Assert.Equal(JsonValueKind.Undefined, grpAfter.ValueKind);

        var orphan = afterDoc
            .RootElement.GetProperty("result")
            .GetProperty("standalones")
            .EnumerateArray()
            .FirstOrDefault(d => d.GetProperty("slug").GetString() == "kpi-board");
        Assert.Equal("kpi-board", orphan.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task DeleteDefaultGroup_ReturnsValidationError()
    {
        var resp = await _client.DeleteAsync(
            $"/api/workspaces/{_workspaceSlug}/dashboard-groups/operations"
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private async Task WaitForSeed()
    {
        // Poll the nav endpoint until "home" shows up or we hit the cap.
        // Fire-and-forget seeding can lag, especially on a busy CI box where
        // many integration tests are creating + tearing down their own WAFs
        // back-to-back. 15s gives generous slack while still failing loudly
        // if the seeder is genuinely broken.
        for (var i = 0; i < 150; i++)
        {
            var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/dashboards/");
            if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(
                    await resp.Content.ReadAsStreamAsync()
                );
                var tree = doc.RootElement.GetProperty("result");
                var standalones = tree.GetProperty("standalones");
                if (
                    standalones
                        .EnumerateArray()
                        .Any(d => d.GetProperty("slug").GetString() == "home")
                )
                    return;
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException(
            "Dashboard seeder did not run within 15 seconds of workspace create."
        );
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
