using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Scripting.ToolLoop;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

/// <summary>
/// Exercises <see cref="WorkspaceToolLoopRegistry"/>'s tool building +
/// in-process invocation. The tools wrap real filesystem operations against
/// a temp working tree, so each test gets a fresh directory and seeds the
/// state it needs.
/// </summary>
public class WorkspaceToolLoopRegistryTests : IAsyncLifetime
{
    private static readonly WorkspaceToolLoopRegistry Registry = new();
    private string _workingTree = null!;
    private ToolLogSink _sink = null!;
    private StepContext _ctx = null!;

    public Task InitializeAsync()
    {
        _workingTree = Path.Combine(Path.GetTempPath(), $"creuser-tlr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingTree);
        _sink = new ToolLogSink();
        _ctx = new StepContext(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "ws",
            WorkingTreePath: _workingTree,
            StepId: Guid.NewGuid(),
            StepName: "tlr-test",
            Budgets: new StepBudgets(),
            Logger: NullLogger.Instance,
            AllowedCommands: null,
            RequiredSecrets: null,
            ResumeToken: null
        );
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
            // best-effort
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void AvailableTools_ListsExpectedNames()
    {
        Assert.Contains("read_file", Registry.AvailableTools);
        Assert.Contains("list_directory", Registry.AvailableTools);
        Assert.Contains("grep", Registry.AvailableTools);
        Assert.Contains("find_files_by_pattern", Registry.AvailableTools);
        Assert.Contains("git_log", Registry.AvailableTools);
    }

    [Fact]
    public void BuildTools_RejectsUnknownToolName()
    {
        var ex = Assert.Throws<ToolLoopException>(() =>
            Registry.BuildTools(["nonsense"], _ctx, _sink)
        );
        Assert.Contains("Unknown tool", ex.Message);
    }

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        var path = Path.Combine(_workingTree, "hello.txt");
        await File.WriteAllTextAsync(path, "hello-world");

        var tools = Registry.BuildTools(["read_file"], _ctx, _sink);
        var read = tools.Single(t => t.Name == "read_file");
        var result = await read.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?> { ["path"] = "hello.txt", ["max_bytes"] = null }
            )
        );
        var json = JsonSerializer.Serialize(result);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("hello-world", doc.RootElement.GetProperty("content").GetString());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Single(_sink.Entries);
        Assert.Equal("read_file", _sink.Entries[0].Tool);
    }

    [Fact]
    public async Task ReadFile_TruncatesAtMaxBytes()
    {
        var path = Path.Combine(_workingTree, "long.txt");
        await File.WriteAllTextAsync(path, new string('a', 5000));

        var tools = Registry.BuildTools(["read_file"], _ctx, _sink);
        var read = tools.Single();
        var result = await read.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?> { ["path"] = "long.txt", ["max_bytes"] = 100 }
            )
        );
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(100, doc.RootElement.GetProperty("content").GetString()!.Length);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ReadFile_PathEscape_ReturnsFatal()
    {
        var tools = Registry.BuildTools(["read_file"], _ctx, _sink);
        var read = tools.Single();
        var result = await read.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "../../etc/passwd",
                    ["max_bytes"] = null,
                }
            )
        );
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("fatal").GetBoolean());
        Assert.True(_sink.FatalEncountered);
    }

    [Fact]
    public async Task ListDirectory_NonRecursive_ReturnsImmediateChildren()
    {
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "a.md"), "x");
        Directory.CreateDirectory(Path.Combine(_workingTree, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "sub", "b.md"), "y");

        var tools = Registry.BuildTools(["list_directory"], _ctx, _sink);
        var list = tools.Single();
        var result = await list.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["recursive"] = false,
                    ["max_entries"] = null,
                }
            )
        );
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.GetProperty("name").GetString() == "a.md");
        Assert.Contains(entries, e => e.GetProperty("name").GetString() == "sub");
    }

    [Fact]
    public async Task ListDirectory_Recursive_TruncatesAtMaxEntries()
    {
        for (var i = 0; i < 25; i++)
            await File.WriteAllTextAsync(Path.Combine(_workingTree, $"f{i}.txt"), $"{i}");

        var tools = Registry.BuildTools(["list_directory"], _ctx, _sink);
        var list = tools.Single();
        var result = await list.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = ".",
                    ["recursive"] = true,
                    ["max_entries"] = 5,
                }
            )
        );
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(5, doc.RootElement.GetProperty("entries").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task FindFilesByPattern_ReturnsMatchingPaths()
    {
        Directory.CreateDirectory(Path.Combine(_workingTree, "src"));
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "src", "a.cs"), "x");
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "src", "b.cs"), "y");
        await File.WriteAllTextAsync(Path.Combine(_workingTree, "src", "c.md"), "z");

        var tools = Registry.BuildTools(["find_files_by_pattern"], _ctx, _sink);
        var find = tools.Single();
        var result = await find.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?> { ["glob"] = "src/**/*.cs", ["max_results"] = null }
            )
        );
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var files = doc
            .RootElement.GetProperty("files")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains("src/a.cs", files);
        Assert.Contains("src/b.cs", files);
    }

    [Fact]
    public async Task Grep_FindsMatches()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workingTree, "doc.md"),
            "first line\nsecond MATCH line\nthird line\n"
        );

        var tools = Registry.BuildTools(["grep"], _ctx, _sink);
        var grep = tools.Single();
        var result = await grep.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "MATCH",
                    ["path"] = ".",
                    ["file_glob"] = null,
                    ["max_matches"] = null,
                }
            )
        );
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        // grep may not be installed on Windows hosts — accept either a
        // populated match list or the binary-not-found error envelope.
        if (
            doc.RootElement.TryGetProperty("error", out var err)
            && err.GetString()?.Contains("not found") == true
        )
        {
            return;
        }
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var matches = doc.RootElement.GetProperty("matches").EnumerateArray().ToList();
        Assert.NotEmpty(matches);
        Assert.Equal("doc.md", matches[0].GetProperty("file").GetString());
        Assert.Equal(2, matches[0].GetProperty("line").GetInt32());
    }
}
