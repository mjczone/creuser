using System.Text;
using Creuser.Core.Execution;
using Creuser.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

public class FileMutateStepRunnerTests : IAsyncLifetime
{
    private static readonly FileMutateStepRunner Runner = new();
    private string _workingTree = null!;

    public Task InitializeAsync()
    {
        _workingTree = Path.Combine(Path.GetTempPath(), $"creuser-fmutate-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingTree);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_workingTree, recursive: true);
        }
        catch
        {
            // best effort
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void StepType_IsFileMutate()
    {
        Assert.Equal("file-mutate", Runner.StepType);
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
    public async Task Execute_EmptyOpsList_SucceedsWithNoFileChanges()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["ops"] = new List<object?>() },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Empty(result.FileChanges);
        Assert.Equal(0, (int)result.Outputs["applied"]!);
    }

    [Fact]
    public async Task Execute_CreateOp_ReturnsCreateChangeWithoutTouchingDisk()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "create",
                        ["path"] = "new-file.md",
                        ["content"] = "# Hello",
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Single(result.FileChanges);
        var change = result.FileChanges[0];
        Assert.Equal("new-file.md", change.Path);
        Assert.Equal(FileChangeOp.Create, change.Op);
        Assert.NotNull(change.AfterHash);
        Assert.NotNull(change.Content);
        Assert.Equal("# Hello", Encoding.UTF8.GetString(change.Content!));

        // Critical invariant: runner did NOT write the file. The executor's
        // ApplyAndCommitAsync is the only path that touches disk.
        Assert.False(File.Exists(Path.Combine(_workingTree, "new-file.md")));
    }

    [Fact]
    public async Task Execute_CreateOnExistingFile_Fails()
    {
        var existing = Path.Combine(_workingTree, "exists.md");
        await File.WriteAllTextAsync(existing, "old");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "create",
                        ["path"] = "exists.md",
                        ["content"] = "new",
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("already exists", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_ModifyOp_RecordsBeforeAndAfterHashes()
    {
        var path = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(path, "before content");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "modify",
                        ["path"] = "doc.md",
                        ["content"] = "after content",
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var change = result.FileChanges[0];
        Assert.Equal(FileChangeOp.Modify, change.Op);
        Assert.NotNull(change.BeforeHash);
        Assert.NotNull(change.AfterHash);
        Assert.NotEqual(change.BeforeHash, change.AfterHash);
        // Runner doesn't touch disk; original content is preserved.
        Assert.Equal("before content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Execute_ModifyMissingFile_Fails()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "modify",
                        ["path"] = "missing.md",
                        ["content"] = "x",
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_DeleteOp_ProducesDeleteChange()
    {
        var path = Path.Combine(_workingTree, "old.txt");
        await File.WriteAllTextAsync(path, "bye");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["op"] = "delete", ["path"] = "old.txt" },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(FileChangeOp.Delete, result.FileChanges[0].Op);
        Assert.NotNull(result.FileChanges[0].BeforeHash);
        // Disk untouched.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Execute_RenameOp_RequiresRenameTo()
    {
        var path = Path.Combine(_workingTree, "src.md");
        await File.WriteAllTextAsync(path, "x");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["op"] = "rename", ["path"] = "src.md" },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("rename_to", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_RenameOp_BasicMoveProducesRenameChange()
    {
        var path = Path.Combine(_workingTree, "src.md");
        await File.WriteAllTextAsync(path, "content");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "rename",
                        ["path"] = "src.md",
                        ["rename_to"] = "docs/dest.md",
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var change = result.FileChanges[0];
        Assert.Equal(FileChangeOp.Rename, change.Op);
        Assert.Equal("src.md", change.Path);
        Assert.Equal("docs/dest.md", change.RenameTo);
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
                        ["op"] = "create",
                        ["path"] = "../../etc/passwd",
                        ["content"] = "x",
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("escapes the workspace root", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_UnknownOp_Refused()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "transmogrify",
                        ["path"] = "foo.md",
                    },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("not a recognised operation", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_MultipleOps_AllChangesReturnedInOrder()
    {
        var doc = Path.Combine(_workingTree, "doc.md");
        await File.WriteAllTextAsync(doc, "original");
        var trash = Path.Combine(_workingTree, "trash.txt");
        await File.WriteAllTextAsync(trash, "old");

        var result = await Runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["ops"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["op"] = "create",
                        ["path"] = "fresh.md",
                        ["content"] = "new",
                    },
                    new Dictionary<string, object?>
                    {
                        ["op"] = "modify",
                        ["path"] = "doc.md",
                        ["content"] = "updated",
                    },
                    new Dictionary<string, object?> { ["op"] = "delete", ["path"] = "trash.txt" },
                },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(3, result.FileChanges.Count);
        Assert.Equal(FileChangeOp.Create, result.FileChanges[0].Op);
        Assert.Equal(FileChangeOp.Modify, result.FileChanges[1].Op);
        Assert.Equal(FileChangeOp.Delete, result.FileChanges[2].Op);
        Assert.Equal(3, (int)result.Outputs["applied"]!);
    }

    private StepContext BuildContext()
    {
        return new StepContext(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "test-ws",
            WorkingTreePath: _workingTree,
            StepId: Guid.NewGuid(),
            StepName: "file-mutate test",
            Budgets: new StepBudgets(),
            Logger: NullLogger.Instance,
            AllowedCommands: null,
            RequiredSecrets: null,
            ResumeToken: null
        );
    }
}
