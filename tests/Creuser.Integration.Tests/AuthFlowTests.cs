using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

public sealed class AuthFlowTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;

    public AuthFlowTests(PostgresFixture pg)
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
    public async Task Anonymous_Me_Returns_Unauthorized()
    {
        var resp = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_Admin_Can_Login_And_See_Self()
    {
        var login = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "admin@creuser.test", password = "ChangeMe!" }
        );
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var me = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadAsStringAsync();
        Assert.Contains("\"role\":\"Admin\"", body);
        Assert.Contains("\"mustChangePassword\":true", body);
    }

    [Fact]
    public async Task Admin_Invite_Returns_Temp_Password_And_Invitee_Can_Login()
    {
        await Login("admin@creuser.test", "ChangeMe!");

        var create = await _client.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                email = "invitee@example.com",
                displayName = "Invitee",
                role = "User",
            }
        );
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync());
        var temp = doc
            .RootElement.GetProperty("result")
            .GetProperty("temporaryPassword")
            .GetString();
        Assert.False(string.IsNullOrEmpty(temp));

        // Logout admin, login as invitee with temp password.
        await _client.PostAsync("/api/auth/logout", null);
        var inviteeLogin = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "invitee@example.com", password = temp }
        );
        Assert.Equal(HttpStatusCode.OK, inviteeLogin.StatusCode);

        var meBody = await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync();
        Assert.Contains("\"mustChangePassword\":true", meBody);
    }

    [Fact]
    public async Task NonAdmin_Cannot_Hit_Admin_Endpoints()
    {
        // Create + login as a regular user.
        await Login("admin@creuser.test", "ChangeMe!");
        var create = await _client.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                email = "user@example.com",
                displayName = "User",
                role = "User",
                temporaryPassword = "TempPass99",
            }
        );
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        await _client.PostAsync("/api/auth/logout", null);

        await Login("user@example.com", "TempPass99");
        var listAttempt = await _client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, listAttempt.StatusCode);
    }

    [Fact]
    public async Task Bad_Password_Returns_Invalid_Credentials_ProblemDetails()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "admin@creuser.test", password = "wrong" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid-credentials", body);
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
