using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end multi-step DAG tests. The job's frontmatter declares a
/// <c>steps:</c> array; the executor walks them topologically, resolves
/// <c>$step_id.field</c> bindings, and persists per-step audit records.
/// Cancellation propagation, binding errors, and DAG validation failures
/// are all observable via the run-detail API.
/// </summary>
public sealed class MultiStepDagIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public MultiStepDagIntegrationTests(PostgresFixture pg)
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

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-dag-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _workspaceSlug = $"dag-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "DAG Test Workspace",
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
    public async Task TwoStepDag_FileMutateThenFileMutate_BothStepsRecorded()
    {
        // Two-step job: first creates a marker file, second creates a
        // second marker file. No binding between them — just exercises
        // the multi-step persistence + topological walk.
        var jobId = await CreateDagJob(
            slug: "two-step",
            stepsYaml: """
              - id: a
                type: file-mutate
                inputs:
                  ops:
                    - op: create
                      path: a.txt
                      content: from-step-a
              - id: b
                type: file-mutate
                depends_on:
                  - a
                inputs:
                  ops:
                    - op: create
                      path: b.txt
                      content: from-step-b
            """
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

        // Both files exist on disk.
        Assert.True(File.Exists(Path.Combine(_workspacePath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_workspacePath, "b.txt")));

        // Run detail: two step records, in order, both succeeded.
        var runId = doc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var steps = await GetSteps(runId);
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("a", steps[0].GetProperty("name").GetString());
        Assert.Equal("succeeded", steps[0].GetProperty("status").GetString());
        Assert.Equal(0, steps[0].GetProperty("position").GetInt32());
        Assert.Equal("b", steps[1].GetProperty("name").GetString());
        Assert.Equal("succeeded", steps[1].GetProperty("status").GetString());
        Assert.Equal(1, steps[1].GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task DagWithBinding_DownstreamReadsUpstreamOutput()
    {
        // Step `gen` (shell) prints a fixed string; step `write`
        // (file-mutate) reads the upstream `stdout` via $gen.stdout and
        // writes it as a file. Verifies the binding resolver wires
        // outputs forward through the executor.
        if (!IsBashAvailable())
            return;

        var jobId = await CreateDagJob(
            slug: "binding-flow",
            stepsYaml: """
              - id: gen
                type: shell
                inputs:
                  script: "echo bound-via-step-output"
              - id: write
                type: file-mutate
                depends_on:
                  - gen
                inputs:
                  ops:
                    - op: create
                      path: marker.txt
                      content: $gen.stdout
            """,
            allowedCommands: new[] { "echo" }
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

        var marker = Path.Combine(_workspacePath, "marker.txt");
        Assert.True(File.Exists(marker));
        // shell stdout includes the trailing newline; the binding wires
        // the full string through.
        var content = await File.ReadAllTextAsync(marker);
        Assert.Contains("bound-via-step-output", content);
    }

    [Fact]
    public async Task DagFailureCancelsDownstream()
    {
        // Step `bad` deliberately fails (exit 1). Step `after` depends on
        // it and must end up Cancelled, not Failed. Step `independent`
        // doesn't depend on `bad` so it runs to completion. Step `late`
        // depends on `after` so the cancellation propagates transitively.
        if (!IsBashAvailable())
            return;

        var jobId = await CreateDagJob(
            slug: "cancellation",
            stepsYaml: """
              - id: bad
                type: shell
                inputs:
                  script: "exit 1"
              - id: independent
                type: shell
                inputs:
                  script: "echo i-ran"
              - id: after
                type: shell
                depends_on:
                  - bad
                inputs:
                  script: "echo never"
              - id: late
                type: shell
                depends_on:
                  - after
                inputs:
                  script: "echo also-never"
            """,
            allowedCommands: new[] { "echo", "exit" }
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "failed",
            doc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );

        var runId = doc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var steps = await GetSteps(runId);
        var byName = new Dictionary<string, JsonElement>();
        foreach (var s in steps.EnumerateArray())
            byName[s.GetProperty("name").GetString()!] = s;

        Assert.Equal("failed", byName["bad"].GetProperty("status").GetString());
        Assert.Equal("succeeded", byName["independent"].GetProperty("status").GetString());
        Assert.Equal("cancelled", byName["after"].GetProperty("status").GetString());
        Assert.Equal("cancelled", byName["late"].GetProperty("status").GetString());

        // Cancelled step's error message names the upstream that blocked it.
        Assert.Contains(
            "upstream step 'bad'",
            byName["after"].GetProperty("errorMessage").GetString()
        );
    }

    [Fact]
    public async Task DagWithCycle_RunFails_AuditCarriesValidationError()
    {
        var jobId = await CreateDagJob(
            slug: "cyclic",
            stepsYaml: """
              - id: a
                type: shell
                depends_on:
                  - b
                inputs:
                  script: echo a
              - id: b
                type: shell
                depends_on:
                  - a
                inputs:
                  script: echo b
            """,
            allowedCommands: new[] { "echo" }
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "failed",
            doc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );
        Assert.Contains(
            "cycle",
            doc.RootElement.GetProperty("result").GetProperty("failureMessage").GetString()
        );

        // The audit timeline carries the validation error as a single step.
        var runId = doc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var steps = await GetSteps(runId);
        Assert.Equal(1, steps.GetArrayLength());
        Assert.Equal("_dag_validation", steps[0].GetProperty("stepType").GetString());
        Assert.Contains("cycle", steps[0].GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task DagWithBindingTypo_StepFailsCleanly()
    {
        if (!IsBashAvailable())
            return;

        var jobId = await CreateDagJob(
            slug: "binding-typo",
            stepsYaml: """
              - id: gen
                type: shell
                inputs:
                  script: "echo hi"
              - id: write
                type: file-mutate
                depends_on:
                  - gen
                inputs:
                  ops:
                    - op: create
                      path: out.txt
                      content: $gen.this_field_does_not_exist
            """,
            allowedCommands: new[] { "echo" }
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "failed",
            doc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );
        Assert.Contains(
            "this_field_does_not_exist",
            doc.RootElement.GetProperty("result").GetProperty("failureMessage").GetString()
        );

        // The upstream `gen` step still ran successfully — only the
        // downstream `write` step failed at the binding boundary.
        var runId = doc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var steps = await GetSteps(runId);
        var byName = new Dictionary<string, JsonElement>();
        foreach (var s in steps.EnumerateArray())
            byName[s.GetProperty("name").GetString()!] = s;
        Assert.Equal("succeeded", byName["gen"].GetProperty("status").GetString());
        Assert.Equal("failed", byName["write"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task DagWithParametersBinding_ResolvesPerRunInputs()
    {
        // Job parameter `marker` is referenced via $params.marker in a
        // file-mutate op's content. The per-run parameters supplied at
        // trigger time flow through the binding resolver.
        var jobId = await CreateDagJob(
            slug: "params-binding",
            stepsYaml: """
              - id: write
                type: file-mutate
                inputs:
                  ops:
                    - op: create
                      path: param-driven.txt
                      content: $params.marker
            """
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { marker = "supplied-at-trigger-time" } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        Assert.Equal(
            "succeeded",
            doc.RootElement.GetProperty("result").GetProperty("status").GetString()
        );

        var content = await File.ReadAllTextAsync(Path.Combine(_workspacePath, "param-driven.txt"));
        Assert.Equal("supplied-at-trigger-time", content);
    }

    private async Task<JsonElement> GetSteps(Guid runId)
    {
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        detail.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await detail.Content.ReadAsStreamAsync());
        // Hold the document open by cloning the steps array we hand back.
        var steps = doc.RootElement.GetProperty("result").GetProperty("steps").Clone();
        doc.Dispose();
        return steps;
    }

    private async Task<Guid> CreateDagJob(
        string slug,
        string stepsYaml,
        string[]? allowedCommands = null
    )
    {
        var allowedYaml = string.Empty;
        if (allowedCommands is { Length: > 0 })
            allowedYaml =
                "allowed_commands:\n" + string.Join("", allowedCommands.Select(c => $"  - {c}\n"));

        var frontmatter = "pattern: deterministic\n" + allowedYaml + "steps:\n" + stepsYaml;

        var resp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/",
            new
            {
                slug,
                name = slug,
                description = (string?)null,
                pattern = "deterministic",
                frontmatter,
                body = "",
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
