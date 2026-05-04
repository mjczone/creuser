using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end test for the plugin loader. Publishes the Hello example
/// plugin into a temp data directory, points the host's CREUSER_DATA_DIR
/// at it, boots the host, and asserts:
///
/// <list type="bullet">
///   <item>The plugin appears in <c>cr.plugins</c> with status <c>loaded</c>.</item>
///   <item>The plugin's <c>hello-world</c> step runner is invocable end-to-end via the jobs API.</item>
///   <item>The workspace plugins endpoint exposes the plugin and its enablement state.</item>
///   <item>Toggling enablement via the PUT endpoint persists.</item>
/// </list>
/// </summary>
public sealed class PluginLoaderIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dataDir = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public PluginLoaderIntegrationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        // Build + stage the Hello plugin into a fresh per-test data dir
        // so we don't depend on the workspace's `.data/plugins/` state.
        _dataDir = Path.Combine(Path.GetTempPath(), $"creuser-plugin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var pluginDir = Path.Combine(_dataDir, "plugins", "creuser.examples.hello");
        Directory.CreateDirectory(pluginDir);
        StagePluginFromBuildOutput(pluginDir);

        _factory = new CreuserApiFactory
        {
            ConnectionString = _pg.ConnectionString,
            DataDir = _dataDir,
        };
        _client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            }
        );
        await Login("admin@creuser.test", "ChangeMe!");

        _workspacePath = Path.Combine(_dataDir, "workspace");
        Directory.CreateDirectory(_workspacePath);
        _workspaceSlug = $"plg-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Plugin Test Workspace",
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
            Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // best effort
        }
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task PluginAppearsInListWithLoadedStatus()
    {
        var resp = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/plugins");
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var plugins = doc.RootElement.GetProperty("result").GetProperty("plugins");
        Assert.True(plugins.GetArrayLength() >= 1, "Expected at least one discovered plugin.");
        var hello = plugins
            .EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("pluginId").GetString() == "creuser.examples.hello");
        Assert.NotEqual(JsonValueKind.Undefined, hello.ValueKind);
        Assert.Equal("loaded", hello.GetProperty("status").GetString());
        Assert.False(hello.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task EnableDisablePersists()
    {
        // Enable.
        var enableResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.hello",
            new { enabled = true }
        );
        enableResp.EnsureSuccessStatusCode();

        var listAfterEnable = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/plugins");
        using var enabledDoc = await JsonDocument.ParseAsync(
            await listAfterEnable.Content.ReadAsStreamAsync()
        );
        var hello = enabledDoc
            .RootElement.GetProperty("result")
            .GetProperty("plugins")
            .EnumerateArray()
            .First(p => p.GetProperty("pluginId").GetString() == "creuser.examples.hello");
        Assert.True(hello.GetProperty("enabled").GetBoolean());

        // Disable.
        var disableResp = await _client.PutAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/plugins/creuser.examples.hello",
            new { enabled = false }
        );
        disableResp.EnsureSuccessStatusCode();

        var listAfterDisable = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/plugins");
        using var disabledDoc = await JsonDocument.ParseAsync(
            await listAfterDisable.Content.ReadAsStreamAsync()
        );
        var helloOff = disabledDoc
            .RootElement.GetProperty("result")
            .GetProperty("plugins")
            .EnumerateArray()
            .First(p => p.GetProperty("pluginId").GetString() == "creuser.examples.hello");
        Assert.False(helloOff.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task HelloWorldStepRunnerExecutesEndToEnd()
    {
        // Plugin contributes a step runner; verify a job using `type:
        // hello-world` runs through JobExecutor → saga → dispatch handler
        // → plugin-contributed runner → success.
        var jobResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug = "say-hello",
                name = "say hello",
                description = (string?)null,
                pattern = "deterministic",
                frontmatter = "type: hello-world\ninputs:\n  name: Creuser\n",
                body = "",
                status = "active",
            }
        );
        jobResp.EnsureSuccessStatusCode();
        using var jobDoc = await JsonDocument.ParseAsync(await jobResp.Content.ReadAsStreamAsync());
        var jobId = jobDoc.RootElement.GetProperty("result").GetProperty("jobScriptId").GetGuid();

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var runDoc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "succeeded",
            runDoc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );

        var runId = runDoc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        Assert.Equal("hello-world", step.GetProperty("stepType").GetString());
        Assert.Equal("succeeded", step.GetProperty("status").GetString());
        var outputsJson = step.GetProperty("outputsJson").GetString()!;
        using var outputsDoc = JsonDocument.Parse(outputsJson);
        Assert.Equal("Hello, Creuser!", outputsDoc.RootElement.GetProperty("greeting").GetString());
    }

    /// <summary>
    /// Locate the Hello plugin's published output and copy it into the
    /// test's plugin directory. The plugin must already be published
    /// (npm run build:plugins:examples) before tests run; otherwise we
    /// look for the plugin's bin output and copy from there.
    /// </summary>
    private static void StagePluginFromBuildOutput(string targetDir)
    {
        // Try the npm-script publish target first (.data/plugins/...);
        // fall back to the plugin's own publish output if that's not set
        // up; finally fall back to the bin output (raw build, not publish).
        var solutionRoot = FindSolutionRoot();
        var candidates = new[]
        {
            Path.Combine(solutionRoot, ".data", "plugins", "creuser.examples.hello"),
            Path.Combine(
                solutionRoot,
                "src",
                "plugins",
                "Creuser.Plugins.Examples.Hello",
                "bin",
                "Debug",
                "net10.0",
                "publish"
            ),
            Path.Combine(
                solutionRoot,
                "src",
                "plugins",
                "Creuser.Plugins.Examples.Hello",
                "bin",
                "Debug",
                "net10.0"
            ),
            Path.Combine(
                solutionRoot,
                "src",
                "plugins",
                "Creuser.Plugins.Examples.Hello",
                "bin",
                "Release",
                "net10.0",
                "publish"
            ),
            Path.Combine(
                solutionRoot,
                "src",
                "plugins",
                "Creuser.Plugins.Examples.Hello",
                "bin",
                "Release",
                "net10.0"
            ),
        };

        var source = candidates.FirstOrDefault(p =>
            Directory.Exists(p)
            && File.Exists(Path.Combine(p, "Creuser.Plugins.Examples.Hello.dll"))
        );
        if (source is null)
            throw new InvalidOperationException(
                "Hello plugin build output not found. Run `npm run build:plugins:examples` "
                    + "or build src/plugins/Creuser.Plugins.Examples.Hello before running this test."
            );

        // Copy only the plugin's own DLL (the framework + host refs are
        // already loaded by the host process; we don't want the loader
        // to redundantly load them from the plugin folder).
        var helloDll = Path.Combine(source, "Creuser.Plugins.Examples.Hello.dll");
        File.Copy(helloDll, Path.Combine(targetDir, "Creuser.Plugins.Examples.Hello.dll"), true);
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Creuser.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not find solution root from " + AppContext.BaseDirectory
        );
    }

    private async Task Login(string email, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();
    }
}
