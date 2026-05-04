using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Creuser.Integration.Tests;

/// <summary>
/// Membership-aware authorization for the v0.1 read surfaces. Verifies:
///
/// <list type="bullet">
///   <item>Non-admin users see only workspaces they're members of (admins see all).</item>
///   <item>Non-admin GET /api/workspaces/{slug} returns 404 for workspaces they don't belong to.</item>
///   <item>Adding a non-admin as a member makes both list + get visible.</item>
///   <item>Non-admin reads of dashboards (list + get) gate the same way.</item>
///   <item>Mutations stay admin-only (covered by the existing endpoint tests; this class
///         confirms the non-admin-blocked path explicitly).</item>
/// </list>
///
/// Editor-level mutations within a workspace stay admin-only in v0.1 — the architecture's
/// "Editors can mutate" intent is a v0.2 follow-up applied to all per-workspace mutation
/// endpoints in one focused slice.
/// </summary>
public sealed class NonAdminAuthGatingTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _adminClient = null!;
    private HttpClient _userClient = null!;
    private string _accessibleSlug = null!;
    private string _accessiblePath = null!;
    private string _hiddenSlug = null!;
    private string _hiddenPath = null!;
    private Guid _userId;

    public NonAdminAuthGatingTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _factory = new CreuserApiFactory { ConnectionString = _pg.ConnectionString };
        _adminClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await Login(_adminClient, "admin@creuser.test", "ChangeMe!");

        // Make a non-admin user.
        var email = $"member-{Guid.NewGuid():N}"[..16] + "@creuser.test";
        _userId = await CreateUser(email, "Member User");

        // Two workspaces — one the user will be a member of, one they won't.
        _accessibleSlug = $"acc-{Guid.NewGuid():N}"[..16];
        _accessiblePath = Path.Combine(Path.GetTempPath(), $"creuser-acc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_accessiblePath);
        await CreateWorkspace(_accessibleSlug, _accessiblePath);

        _hiddenSlug = $"hid-{Guid.NewGuid():N}"[..16];
        _hiddenPath = Path.Combine(Path.GetTempPath(), $"creuser-hid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_hiddenPath);
        await CreateWorkspace(_hiddenSlug, _hiddenPath);

        // Sign in the non-admin user in a separate cookie jar.
        _userClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await Login(_userClient, email, "ChangeMe!");
    }

    public Task DisposeAsync()
    {
        _adminClient.Dispose();
        _userClient.Dispose();
        try
        {
            Directory.Delete(_accessiblePath, true);
        }
        catch { }
        try
        {
            Directory.Delete(_hiddenPath, true);
        }
        catch { }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task NonAdmin_WithNoMemberships_SeesEmptyWorkspaceList()
    {
        var resp = await _userClient.GetAsync("/api/workspaces/");
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task NonAdmin_GettingHiddenWorkspace_Returns404()
    {
        var resp = await _userClient.GetAsync($"/api/workspaces/{_hiddenSlug}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_AfterMembership_SeesWorkspace()
    {
        await GrantMembership(_accessibleSlug, _userId, "Editor");

        var listResp = await _userClient.GetAsync("/api/workspaces/");
        listResp.EnsureSuccessStatusCode();
        using var listDoc = await JsonDocument.ParseAsync(
            await listResp.Content.ReadAsStreamAsync()
        );
        var list = listDoc.RootElement.GetProperty("result");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(_accessibleSlug, list[0].GetProperty("slug").GetString());

        var getResp = await _userClient.GetAsync($"/api/workspaces/{_accessibleSlug}");
        getResp.EnsureSuccessStatusCode();

        // Hidden workspace still 404s for them.
        var hiddenResp = await _userClient.GetAsync($"/api/workspaces/{_hiddenSlug}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenResp.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_AfterMembership_CanFetchDashboardNavTree()
    {
        await GrantMembership(_accessibleSlug, _userId, "Viewer");

        // Seeder is fire-and-forget — poll the user's dashboards endpoint
        // until Home shows up or we hit the cap.
        for (var i = 0; i < 150; i++)
        {
            var resp = await _userClient.GetAsync($"/api/workspaces/{_accessibleSlug}/dashboards/");
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var standalones = doc.RootElement.GetProperty("result").GetProperty("standalones");
            if (standalones.EnumerateArray().Any(d => d.GetProperty("slug").GetString() == "home"))
                return;
            await Task.Delay(100);
        }
        Assert.Fail("Home dashboard not visible to member within 15 seconds of grant.");
    }

    [Fact]
    public async Task NonAdmin_DashboardsForHiddenWorkspace_Return404()
    {
        var resp = await _userClient.GetAsync($"/api/workspaces/{_hiddenSlug}/dashboards/");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CreatingWorkspace_Returns403()
    {
        var resp = await _userClient.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = "should-fail",
                name = "Should Fail",
                description = (string?)null,
                type = "local",
                localSettings = new { path = _accessiblePath, writable = true },
            }
        );
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task<Guid> CreateUser(string email, string displayName)
    {
        var resp = await _adminClient.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                email,
                displayName,
                role = "User",
                temporaryPassword = "ChangeMe!",
            }
        );
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("result").GetProperty("userId").GetGuid();
    }

    private async Task CreateWorkspace(string slug, string path)
    {
        var resp = await _adminClient.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug,
                name = slug,
                description = (string?)null,
                type = "local",
                localSettings = new { path, writable = true },
            }
        );
        resp.EnsureSuccessStatusCode();
    }

    private async Task GrantMembership(string slug, Guid userId, string role)
    {
        var resp = await _adminClient.PostAsJsonAsync(
            $"/api/workspaces/{slug}/members/",
            new { userId, role }
        );
        resp.EnsureSuccessStatusCode();
    }

    private async Task Login(HttpClient client, string email, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
