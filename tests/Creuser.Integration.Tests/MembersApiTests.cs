using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Creuser.Integration.Tests;

/// <summary>
/// CRUD round-trip for workspace memberships. Asserts admin-gated
/// add/update/remove flows, role validation, the user-not-found path,
/// and that re-adding the same user upserts (no duplicate-row error).
/// </summary>
public sealed class MembersApiTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public MembersApiTests(PostgresFixture pg)
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

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-mem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        _workspaceSlug = $"mem-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Members Test",
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
        catch { }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task EmptyWorkspace_HasNoMembers()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/members/");
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task AddListRemove_RoundTrip()
    {
        var userId = await CreateUser("alice@creuser.test", "Alice");

        // Add as Editor.
        var addResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/members/",
            new { userId, role = "Editor" }
        );
        addResp.EnsureSuccessStatusCode();
        using var addDoc = await JsonDocument.ParseAsync(await addResp.Content.ReadAsStreamAsync());
        var added = addDoc.RootElement.GetProperty("result");
        Assert.Equal(userId.ToString(), added.GetProperty("userId").GetString());
        Assert.Equal("Editor", added.GetProperty("role").GetString());
        Assert.Equal("alice@creuser.test", added.GetProperty("email").GetString());

        // List should contain her.
        var listResp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/members/");
        using var listDoc = await JsonDocument.ParseAsync(
            await listResp.Content.ReadAsStreamAsync()
        );
        var list = listDoc.RootElement.GetProperty("result");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Alice", list[0].GetProperty("displayName").GetString());

        // Update role to Viewer.
        var updResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/members/{userId}",
            new { role = "Viewer" }
        );
        updResp.EnsureSuccessStatusCode();
        using var updDoc = await JsonDocument.ParseAsync(await updResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "Viewer",
            updDoc.RootElement.GetProperty("result").GetProperty("role").GetString()
        );

        // Remove.
        var delResp = await _client.DeleteAsync(
            $"/api/workspaces/{_workspaceSlug}/members/{userId}"
        );
        delResp.EnsureSuccessStatusCode();
        var listAfter = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/members/");
        using var afterDoc = await JsonDocument.ParseAsync(
            await listAfter.Content.ReadAsStreamAsync()
        );
        Assert.Equal(0, afterDoc.RootElement.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task AddSameUserTwice_Upserts()
    {
        var userId = await CreateUser("bob@creuser.test", "Bob");
        var add1 = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/members/",
            new { userId, role = "Editor" }
        );
        add1.EnsureSuccessStatusCode();
        var add2 = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/members/",
            new { userId, role = "Viewer" }
        );
        add2.EnsureSuccessStatusCode();

        var list = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/members/");
        using var doc = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync());
        var rows = doc.RootElement.GetProperty("result");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("Viewer", rows[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task InvalidRole_Returns400()
    {
        var userId = await CreateUser("carol@creuser.test", "Carol");
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/members/",
            new { userId, role = "Wizard" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownUser_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/members/",
            new { userId = Guid.NewGuid(), role = "Editor" }
        );
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<Guid> CreateUser(string email, string displayName)
    {
        var resp = await _client.PostAsJsonAsync(
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

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
