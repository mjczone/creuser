using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

/// <summary>
/// End-to-end test for the file-mutate runner + the executor's
/// transactional apply path. Uses a local-type workspace so there's no git
/// commit involved — that exercises the apply path without requiring a
/// remote. Git workspace integration coverage requires a fake bare-repo
/// origin and is reserved for a follow-up slice.
/// </summary>
public sealed class FileMutateIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public FileMutateIntegrationTests(PostgresFixture pg)
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

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-fm-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _workspaceSlug = $"fm-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "File-Mutate Test Workspace",
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
    public async Task FileMutateJob_CreateOp_FileMaterializedInWorkspace()
    {
        // The `inputs.ops` block is YAML — multi-line content has to escape
        // newlines through YamlDotNet's serializer. We use single-line
        // content to keep the YAML simple.
        var jobId = await CreateFileMutateJob(
            slug: "create-marker",
            opsYaml: "  - op: create\n    path: marker.txt\n    content: created-by-file-mutate\n"
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await runResp.Content.ReadAsStreamAsync());
        var status = doc.RootElement.GetProperty("result").GetProperty("status").GetString();
        Assert.Equal("succeeded", status);

        // The executor's ApplyAndCommitAsync must have written the file.
        var marker = Path.Combine(_workspacePath, "marker.txt");
        Assert.True(File.Exists(marker), $"Expected marker file at {marker}");
        var content = await File.ReadAllTextAsync(marker);
        Assert.Equal("created-by-file-mutate", content);

        // Step audit shows the file change count.
        var runId = doc.RootElement.GetProperty("result").GetProperty("runId").GetGuid();
        var detail = await _client.GetAsync($"/api/workspaces/{_workspaceSlug}/runs/{runId}");
        using var detailDoc = await JsonDocument.ParseAsync(
            await detail.Content.ReadAsStreamAsync()
        );
        var step = detailDoc.RootElement.GetProperty("result").GetProperty("steps")[0];
        Assert.Equal(1, step.GetProperty("fileChangeCount").GetInt32());
        // Local workspace: no commit SHA recorded.
        Assert.Equal(JsonValueKind.Null, step.GetProperty("commitSha").ValueKind);
    }

    [Fact]
    public async Task FileMutateJob_ModifyOp_OverwritesExistingFile()
    {
        var existing = Path.Combine(_workspacePath, "doc.md");
        await File.WriteAllTextAsync(existing, "before");

        var jobId = await CreateFileMutateJob(
            slug: "rewrite-doc",
            opsYaml: "  - op: modify\n    path: doc.md\n    content: after\n"
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

        Assert.Equal("after", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task FileMutateJob_DeleteOp_RemovesFile()
    {
        var doomed = Path.Combine(_workspacePath, "doomed.txt");
        await File.WriteAllTextAsync(doomed, "x");

        var jobId = await CreateFileMutateJob(
            slug: "delete-doomed",
            opsYaml: "  - op: delete\n    path: doomed.txt\n"
        );

        await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );

        Assert.False(File.Exists(doomed));
    }

    [Fact]
    public async Task FileMutateJob_RenameOp_MovesFile()
    {
        var src = Path.Combine(_workspacePath, "old-name.md");
        await File.WriteAllTextAsync(src, "stable content");

        var jobId = await CreateFileMutateJob(
            slug: "rename-doc",
            opsYaml: "  - op: rename\n    path: old-name.md\n    rename_to: new/name.md\n"
        );

        await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );

        Assert.False(File.Exists(src));
        var dest = Path.Combine(_workspacePath, "new", "name.md");
        Assert.True(File.Exists(dest));
        Assert.Equal("stable content", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task FileMutateJob_PathEscape_FailsWithoutTouchingDisk()
    {
        var jobId = await CreateFileMutateJob(
            slug: "escape-attempt",
            opsYaml: "  - op: create\n    path: ../../etc/danger.txt\n    content: x\n"
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
            "escapes the workspace root",
            doc.RootElement.GetProperty("result").GetProperty("failureMessage").GetString()
        );
    }

    [Fact]
    public async Task FileMutateJob_MultipleOps_AllAppliedAtomically()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspacePath, "keep.md"), "old");
        await File.WriteAllTextAsync(Path.Combine(_workspacePath, "trash.txt"), "bye");

        var jobId = await CreateFileMutateJob(
            slug: "multi-op",
            opsYaml: "  - op: create\n    path: fresh.md\n    content: new-content\n"
                + "  - op: modify\n    path: keep.md\n    content: updated\n"
                + "  - op: delete\n    path: trash.txt\n"
        );

        await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );

        Assert.True(File.Exists(Path.Combine(_workspacePath, "fresh.md")));
        Assert.Equal(
            "new-content",
            await File.ReadAllTextAsync(Path.Combine(_workspacePath, "fresh.md"))
        );
        Assert.Equal(
            "updated",
            await File.ReadAllTextAsync(Path.Combine(_workspacePath, "keep.md"))
        );
        Assert.False(File.Exists(Path.Combine(_workspacePath, "trash.txt")));
    }

    private async Task<Guid> CreateFileMutateJob(string slug, string opsYaml)
    {
        var frontmatter = $"type: file-mutate\ninputs:\n  ops:\n{opsYaml}";
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
}
