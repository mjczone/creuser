using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end test that creates a local-type workspace, drops a job that
/// runs a shell command, triggers it, and asserts the run is recorded with
/// the right status, exit code, and audit shape. Exercises the full chain:
/// Jobs API → JobExecutor → IWorkspaceWorkingTree resolution →
/// ShellStepRunner → JobRunStep persistence.
/// </summary>
public sealed class ShellRunnerIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public ShellRunnerIntegrationTests(PostgresFixture pg)
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

        // Use a per-test temp directory as the local workspace path so we
        // don't depend on git being configured + so each test gets clean
        // state to write into.
        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-shell-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _workspaceSlug = $"shell-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Shell Test Workspace",
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
            // best-effort
        }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task RunShellJob_EchoCommand_RecordsRunWithExitCodeZero()
    {
        if (!IsBashAvailable())
            return;

        var jobId = await CreateShellJob(
            slug: "echo-hello",
            allowedCommands: new[] { "echo" },
            body: "echo hello-from-creuser"
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("succeeded", result.GetProperty("status").GetString());

        // Fetch the run detail to confirm the step record was persisted with
        // the audit fields populated.
        var runId = result.GetProperty("runId").GetGuid();
        var detailResp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        using var detail = await JsonDocument.ParseAsync(
            await detailResp.Content.ReadAsStreamAsync()
        );
        var steps = detail.RootElement.GetProperty("result").GetProperty("steps");
        Assert.Equal(1, steps.GetArrayLength());

        var step = steps[0];
        Assert.Equal("shell", step.GetProperty("stepType").GetString());
        Assert.Equal("succeeded", step.GetProperty("status").GetString());
        Assert.True(step.TryGetProperty("idempotencyKey", out var keyProp));
        Assert.False(string.IsNullOrEmpty(keyProp.GetString()));

        // Outputs JSON carries exit code + stdout text. Postgres jsonb
        // reformats with spaces around the colons; parse to verify the
        // structural content rather than asserting against raw substring.
        var outputsJson = step.GetProperty("outputsJson").GetString();
        Assert.Contains("hello-from-creuser", outputsJson);
        using var outputsDoc = JsonDocument.Parse(outputsJson!);
        Assert.Equal(0, outputsDoc.RootElement.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task RunShellJob_DisallowedCommand_FailsBeforeExecution()
    {
        var jobId = await CreateShellJob(
            slug: "blocked-rm",
            allowedCommands: new[] { "echo" }, // doesn't include rm
            body: "rm -rf /"
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("failed", result.GetProperty("status").GetString());
        var failureMessage = result.GetProperty("failureMessage").GetString();
        Assert.Contains("not in allow-list", failureMessage);
        Assert.Contains("rm", failureMessage);

        // The directory must remain untouched — confirm the test workspace
        // still exists. This isn't a real safety test for rm-rf but is a
        // sanity check that the allow-list halts execution before any
        // process spawns.
        Assert.True(Directory.Exists(_workspacePath));
    }

    [Fact]
    public async Task RunShellJob_NoAllowList_FailsImmediately()
    {
        // Build a frontmatter without `allowed_commands` at all.
        var createResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "no-allow-list",
                name = "No allow list",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: shell\n",
                body = "echo hi",
                status = "active",
            }
        );
        createResp.EnsureSuccessStatusCode();
        using var createDoc = await JsonDocument.ParseAsync(
            await createResp.Content.ReadAsStreamAsync()
        );
        var jobId = createDoc
            .RootElement.GetProperty("result")
            .GetProperty("jobScriptId")
            .GetGuid();

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "failed",
            doc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );
        Assert.Contains(
            "no `allowed_commands`",
            doc.RootElement.GetProperty("result").GetProperty("failureMessage").GetString()
        );
    }

    [Fact]
    public async Task RunShellJob_WritesFile_FilePersistsInWorkingTree()
    {
        if (!IsBashAvailable())
            return;

        var jobId = await CreateShellJob(
            slug: "write-marker",
            allowedCommands: new[] { "echo" },
            body: "echo 'created by creuser run' > marker.txt"
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();

        // The shell runner runs in the workspace working tree (= our temp
        // dir for this local-type workspace). The redirect should land
        // marker.txt there.
        var markerPath = Path.Combine(_workspacePath, "marker.txt");
        Assert.True(File.Exists(markerPath), $"Expected marker file at {markerPath}");
        var content = await File.ReadAllTextAsync(markerPath);
        Assert.Contains("created by creuser run", content);
    }

    private async Task<Guid> CreateShellJob(string slug, string[] allowedCommands, string body)
    {
        var allowedYaml = string.Join("", allowedCommands.Select(c => $"  - {c}\n"));
        var frontmatter = $"type: shell\nallowed_commands:\n{allowedYaml}";
        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug,
                name = slug,
                description = (string?)null,
                pattern = "deterministic",
                frontmatter,
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

    private static bool IsBashAvailable()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("bash", "-c \"true\"")
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
