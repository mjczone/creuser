using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

public sealed class ToolsApiTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;

    public ToolsApiTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync()
    {
        _factory = new CreuserApiFactory { ConnectionString = _pg.ConnectionString };
        _client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task ListTools_RequiresAuth()
    {
        var resp = await _client.GetAsync("/api/tools/");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ListTools_AsAdmin_ReturnsCategorizedBaseline()
    {
        await Login("admin@creuser.test", "ChangeMe!");
        var resp = await _client.GetAsync("/api/tools/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var arr = doc.RootElement.GetProperty("result");
        Assert.True(arr.GetArrayLength() > 20, "baseline catalog should expose at least 20 tools");

        // Spot-check a few entries that we expect from the curated palette.
        var names = new HashSet<string>();
        foreach (var item in arr.EnumerateArray())
        {
            names.Add(item.GetProperty("name").GetString()!);
            // Every entry has the four expected fields.
            Assert.False(string.IsNullOrEmpty(item.GetProperty("category").GetString()));
            Assert.False(string.IsNullOrEmpty(item.GetProperty("source").GetString()));
        }
        Assert.Contains("git", names);
        Assert.Contains("jq", names);
        Assert.Contains("rg", names);
        Assert.Contains("dotnet", names);
        Assert.Contains("python", names);
    }

    [Fact]
    public async Task ListTools_AsNonAdmin_Returns403()
    {
        await Login("admin@creuser.test", "ChangeMe!");
        await _client.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                email = "user@tools.example.com",
                displayName = "Tools User",
                role = "User",
                temporaryPassword = "TempPass99",
            }
        );
        await _client.PostAsync("/api/auth/logout", null);
        await Login("user@tools.example.com", "TempPass99");

        var resp = await _client.GetAsync("/api/tools/");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
