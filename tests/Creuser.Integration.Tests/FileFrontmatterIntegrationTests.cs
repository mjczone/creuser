using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Creuser.Integration.Tests;

public sealed class FileFrontmatterIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private CreuserApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _workspaceSlug = null!;
    private string _workspacePath = null!;

    public FileFrontmatterIntegrationTests(PostgresFixture pg)
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

        _workspacePath = Path.Combine(Path.GetTempPath(), $"creuser-fmm-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _workspaceSlug = $"fmm-{Guid.NewGuid():N}"[..16];
        var createWs = await _client.PostAsJsonAsync(
            "/api/workspaces",
            new
            {
                slug = _workspaceSlug,
                name = "Frontmatter Test Workspace",
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
    public async Task FileFrontmatterJob_AddsBlockToMarkdown_FilePersists()
    {
        var docPath = Path.Combine(_workspacePath, "intro.md");
        await File.WriteAllTextAsync(docPath, "# Intro\n\nBody.\n");

        var jobId = await CreateJob(
            slug: "tag-intro",
            opsYaml: "  - path: intro.md\n"
                + "    set:\n"
                + "      title: Introduction\n"
                + "      category: core\n"
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

        var newContent = await File.ReadAllTextAsync(docPath);
        Assert.StartsWith("---\n", newContent);
        Assert.Contains("title: Introduction", newContent);
        Assert.Contains("category: core", newContent);
        Assert.Contains("# Intro", newContent);
        Assert.Contains("Body.", newContent);
    }

    [Fact]
    public async Task FileFrontmatterJob_TypescriptFile_UsesBlockComment()
    {
        var srcPath = Path.Combine(_workspacePath, "service.ts");
        await File.WriteAllTextAsync(srcPath, "export const x = 1;\n");

        var jobId = await CreateJob(
            slug: "tag-ts-service",
            opsYaml: "  - path: service.ts\n"
                + "    set:\n"
                + "      category: domain\n"
                + "      owner: team-a\n"
        );

        var runResp = await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );
        runResp.EnsureSuccessStatusCode();

        var newContent = await File.ReadAllTextAsync(srcPath);
        Assert.StartsWith("/* ---", newContent);
        Assert.Contains("category: domain", newContent);
        Assert.Contains("--- */", newContent);
        Assert.Contains("export const x = 1;", newContent);
    }

    [Fact]
    public async Task FileFrontmatterJob_PythonWithShebang_PreservesShebang()
    {
        var pyPath = Path.Combine(_workspacePath, "build.py");
        await File.WriteAllTextAsync(pyPath, "#!/usr/bin/env python3\nimport os\nprint('hi')\n");

        var jobId = await CreateJob(
            slug: "tag-build-py",
            opsYaml: "  - path: build.py\n"
                + "    set:\n"
                + "      title: Build script\n"
                + "      category: automation\n"
        );

        await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );

        var newContent = await File.ReadAllTextAsync(pyPath);
        Assert.StartsWith("#!/usr/bin/env python3\n", newContent);
        Assert.Contains("# ---", newContent);
        Assert.Contains("# title: Build script", newContent);
        Assert.Contains("import os", newContent);
        Assert.Contains("print('hi')", newContent);
    }

    [Fact]
    public async Task FileFrontmatterJob_UnsetRemovesKeysFromExistingBlock()
    {
        var docPath = Path.Combine(_workspacePath, "page.md");
        await File.WriteAllTextAsync(
            docPath,
            "---\ntitle: Foo\ndraft: true\ntodo: refactor\n---\n\nBody.\n"
        );

        var jobId = await CreateJob(
            slug: "untag-drafts",
            opsYaml: "  - path: page.md\n" + "    unset:\n" + "      - draft\n" + "      - todo\n"
        );

        await _client.PostAsJsonAsync(
            $"/api/workspaces/{_workspaceSlug}/jobs/{jobId}/run",
            new { parameters = new { } }
        );

        var newContent = await File.ReadAllTextAsync(docPath);
        Assert.Contains("title: Foo", newContent);
        Assert.DoesNotContain("draft", newContent);
        Assert.DoesNotContain("todo", newContent);
    }

    [Fact]
    public async Task FileFrontmatterJob_UnsupportedExtension_FailsCleanly()
    {
        var binPath = Path.Combine(_workspacePath, "data.bin");
        await File.WriteAllBytesAsync(binPath, new byte[] { 0, 1, 2 });

        var jobId = await CreateJob(
            slug: "tag-bin",
            opsYaml: "  - path: data.bin\n    set:\n      k: v\n"
        );

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
            "not a supported frontmatter dialect",
            doc.RootElement.GetProperty("result").GetProperty("failureMessage").GetString()
        );
    }

    private async Task<Guid> CreateJob(string slug, string opsYaml)
    {
        var frontmatter = $"type: file-frontmatter\ninputs:\n  ops:\n{opsYaml}";
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
