using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end test that creates a local-type workspace, drops a job that
/// runs a single-file C# script, triggers it, and asserts the run is
/// recorded with the right status, exit code, and audit shape.
/// </summary>
public sealed class CSharpRunnerIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public CSharpRunnerIntegrationTests(PostgresFixture pg)
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

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-csharp-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _workspaceSlug = $"csh-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "C# Test Workspace",
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
    public async Task RunCSharpJob_Hello_RecordsSuccessWithStdout()
    {
        if (!IsDotnetAvailable())
            return;

        var jobId = await CreateCSharpJob(
            slug: "hello-csharp",
            body: "Console.WriteLine(\"hello-from-csharp-runner\");"
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("succeeded", result.GetProperty("status").GetString());

        // Inspect the run detail so we hit the persisted-step audit shape.
        var runId = result.GetProperty("runId").GetGuid();
        var detailResp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        using var detailDoc = await JsonDocument.ParseAsync(
            await detailResp.Content.ReadAsStreamAsync()
        );
        var steps = detailDoc.RootElement.GetProperty("result").GetProperty("steps");
        Assert.Equal(1, steps.GetArrayLength());

        var step = steps[0];
        Assert.Equal("csharp", step.GetProperty("stepType").GetString());
        Assert.Equal("succeeded", step.GetProperty("status").GetString());
        var outputsJson = step.GetProperty("outputsJson").GetString();
        Assert.Contains("hello-from-csharp-runner", outputsJson);
        using var outputsDoc = JsonDocument.Parse(outputsJson!);
        Assert.Equal(0, outputsDoc.RootElement.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task RunCSharpJob_NonZeroExit_RecordsFailure()
    {
        if (!IsDotnetAvailable())
            return;

        var jobId = await CreateCSharpJob(slug: "exit-five", body: "System.Environment.Exit(5);");

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Contains("exited with code 5", result.GetProperty("failureMessage").GetString());
    }

    [Fact]
    public async Task RunCSharpJob_ReadsWorkspaceFile_SeesContent()
    {
        if (!IsDotnetAvailable())
            return;

        var inputPath = Path.Combine(_workspacePath, "input.txt");
        await File.WriteAllTextAsync(inputPath, "csharp-can-read-this");

        // Use CREUSER_WORKING_TREE explicitly. .NET file-based apps may
        // compile the script into an intermediate build dir before running,
        // so relative paths against `Directory.GetCurrentDirectory()` aren't
        // a reliable interface. The env var the runner sets is.
        var jobId = await CreateCSharpJob(
            slug: "read-input",
            body: "var root = System.Environment.GetEnvironmentVariable(\"CREUSER_WORKING_TREE\")!; Console.WriteLine(System.IO.File.ReadAllText(System.IO.Path.Combine(root, \"input.txt\")));"
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "succeeded",
            doc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );

        // Confirm via the run detail that stdout carried the workspace file's content.
        var runId = doc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        Assert.Contains("csharp-can-read-this", step.GetProperty("outputsJson").GetString());
    }

    private async Task<Guid> CreateCSharpJob(string slug, string body)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug,
                name = slug,
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: csharp\n",
                body,
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

    private static bool IsDotnetAvailable()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            );
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
