using System.Text;
using Creuser.Core.Execution;
using Creuser.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

public class FileFrontmatterStepRunnerTests : IAsyncLifetime
{
    private static readonly FileFrontmatterStepRunner Runner = new();
    private string _workingTree = null!;

    public Task InitializeAsync()
    {
        _workingTree = Path.Combine(Path.GetTempPath(), $"creuser-fmm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingTree);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_workingTree, recursive: true);
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void StepType_IsFileFrontmatter()
    {
        Assert.Equal("file-frontmatter", Runner.StepType);
    }

    [Fact]
    public async Task Execute_NoOps_FailsWithMessage()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>(),
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("`ops`", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_SetOnFileWithoutBlock_AddsBlock()
    {
        var path = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(path, "# Heading\n\nBody.\n");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "doc.md",
                        ["set"] = new Dictionary<string, object?>
                        {
                            ["title"] = "Foo",
                            ["category"] = "core",
                        },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Single(result.FileChanges);
        var change = result.FileChanges[0];
        Assert.Equal(FileChangeOp.Modify, change.Op);
        Assert.NotNull(change.Content);
        var newContent = Encoding.UTF8.GetString(change.Content!);
        Assert.StartsWith("---\n", newContent);
        Assert.Contains("title: Foo", newContent);
        Assert.Contains("category: core", newContent);
        Assert.Contains("# Heading", newContent);
        Assert.Contains("Body.", newContent);

        // Disk untouched — runner returns the change, executor applies.
        Assert.Equal("# Heading\n\nBody.\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Execute_SetOnExistingBlock_MergesKeys()
    {
        var path = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(path, "---\ntitle: Foo\ncategory: legacy\n---\n\nBody.\n");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "doc.md",
                        ["set"] = new Dictionary<string, object?>
                        {
                            // Overwrite category, leave title alone, add new key.
                            ["category"] = "core",
                            ["owner"] = "alice",
                        },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var newContent = Encoding.UTF8.GetString(result.FileChanges[0].Content!);
        Assert.Contains("title: Foo", newContent); // untouched
        Assert.Contains("category: core", newContent); // overwritten
        Assert.Contains("owner: alice", newContent); // added
        Assert.DoesNotContain("category: legacy", newContent);
    }

    [Fact]
    public async Task Execute_UnsetRemovesKeysFromExistingBlock()
    {
        var path = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(
            path,
            "---\ntitle: Foo\ndraft: true\ntodo: refactor\n---\n\nBody.\n"
        );

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "doc.md",
                        ["unset"] = new List<object?> { "draft", "todo" },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var newContent = Encoding.UTF8.GetString(result.FileChanges[0].Content!);
        Assert.Contains("title: Foo", newContent);
        Assert.DoesNotContain("draft", newContent);
        Assert.DoesNotContain("todo", newContent);
    }

    [Fact]
    public async Task Execute_ReplaceOverwritesEntireBlock()
    {
        var path = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(path, "---\ntitle: Old\nkeep: not anymore\n---\n\nBody.\n");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "doc.md",
                        ["replace"] = new Dictionary<string, object?>
                        {
                            ["title"] = "New",
                            ["category"] = "core",
                        },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var newContent = Encoding.UTF8.GetString(result.FileChanges[0].Content!);
        Assert.Contains("title: New", newContent);
        Assert.Contains("category: core", newContent);
        Assert.DoesNotContain("keep:", newContent);
        Assert.DoesNotContain("title: Old", newContent);
    }

    [Fact]
    public async Task Execute_PythonFileWithShebang_PreservesShebang()
    {
        var path = Path.Combine(_workingTree, "build.py");
        await File.WriteAllTextAsync(path, "#!/usr/bin/env python3\nimport os\nprint('hi')\n");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "build.py",
                        ["set"] = new Dictionary<string, object?>
                        {
                            ["title"] = "Build script",
                            ["category"] = "automation",
                        },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var newContent = Encoding.UTF8.GetString(result.FileChanges[0].Content!);
        Assert.StartsWith("#!/usr/bin/env python3", newContent);
        Assert.Contains("# ---", newContent);
        Assert.Contains("# title: Build script", newContent);
        Assert.Contains("import os", newContent);
        Assert.Contains("print('hi')", newContent);
    }

    [Fact]
    public async Task Execute_TypescriptFile_UsesBlockCommentDialect()
    {
        var path = Path.Combine(_workingTree, "service.ts");
        await File.WriteAllTextAsync(path, "export const x = 1;\n");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "service.ts",
                        ["set"] = new Dictionary<string, object?>
                        {
                            ["category"] = "domain",
                            ["owner"] = "team-a",
                        },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var newContent = Encoding.UTF8.GetString(result.FileChanges[0].Content!);
        Assert.StartsWith("/* ---", newContent);
        Assert.Contains("category: domain", newContent);
        Assert.Contains("--- */", newContent);
        Assert.Contains("export const x = 1;", newContent);
    }

    [Fact]
    public async Task Execute_UnsupportedExtension_FailsWithTypedError()
    {
        var path = Path.Combine(_workingTree, "data.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0, 1, 2, 3 });

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "data.bin",
                        ["set"] = new Dictionary<string, object?> { ["k"] = "v" },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("not a supported frontmatter dialect", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_MissingFile_Fails()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "nope.md",
                        ["set"] = new Dictionary<string, object?> { ["k"] = "v" },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_MultipleOpsThatAreNoOp_ReturnsZeroChanges()
    {
        // Setting a key to the same value it already has produces a no-op
        // change (after re-serialization). Verify this is handled silently.
        var path = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(path, "---\ntitle: Foo\n---\n\nBody.\n");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "doc.md",
                        ["set"] = new Dictionary<string, object?> { ["title"] = "Foo" },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Empty(result.FileChanges);
    }

    [Fact]
    public async Task Execute_PathEscape_Refused()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "../../etc/passwd",
                        ["set"] = new Dictionary<string, object?> { ["k"] = "v" },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("escapes the workspace root", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_MultipleOpsExclusive_Refused()
    {
        var path = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(path, "---\ntitle: Foo\n---\n\nBody.\n");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "doc.md",
                        ["set"] = new Dictionary<string, object?> { ["a"] = 1 },
                        ["unset"] = new List<object?> { "title" },
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("only one of `set`", result.ErrorMessage);
    }

    private StepContext BuildContext()
    {
        return new StepContext(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "test-ws",
            WorkingTreePath: _workingTree,
            StepId: Guid.NewGuid(),
            StepName: "file-frontmatter test",
            Budgets: new StepBudgets(),
            Logger: NullLogger.Instance,
            AllowedCommands: null,
            RequiredSecrets: null,
            ResumeToken: null
        );
    }
}
