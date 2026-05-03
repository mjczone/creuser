using Creuser.Core.Execution;
using Creuser.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

public class CSharpStepRunnerTests
{
    private static readonly CSharpStepRunner Runner = new();

    [Fact]
    public void StepType_IsCsharp()
    {
        Assert.Equal("csharp", Runner.StepType);
    }

    [Fact]
    public async Task Execute_NoScript_FailsWithMessage()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot()),
            new Dictionary<string, object?>(),
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("`script`", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_NoWorkingTree_FailsWithMessage()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: ""),
            new Dictionary<string, object?> { ["script"] = "Console.WriteLine(\"hi\");" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("workspace working tree", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_WorkingTreeMissing_FailsWithDiskCheckMessage()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: "/nonexistent/csharp/test/abc"),
            new Dictionary<string, object?> { ["script"] = "Console.WriteLine(\"hi\");" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("does not exist on disk", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_WorkingDirectoryEscapes_Refused()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot()),
            new Dictionary<string, object?>
            {
                ["script"] = "Console.WriteLine(\"hi\");",
                ["working_directory"] = "../../etc",
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("escapes the workspace root", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_HelloWorld_ProducesExitZeroAndStdout()
    {
        if (!IsDotnetAvailable())
            return;

        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot()),
            new Dictionary<string, object?>
            {
                ["script"] = "Console.WriteLine(\"hello-from-creuser-csharp\");",
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(0, (int)result.Outputs["exit_code"]!);
        Assert.Contains("hello-from-creuser-csharp", (string)result.Outputs["stdout"]!);

        // Source artifact captured for replay.
        Assert.Contains(result.Artifacts, a => a.Kind == "source" && a.FileName == "script.cs");
    }

    [Fact]
    public async Task Execute_NonZeroExit_MarksFailed()
    {
        if (!IsDotnetAvailable())
            return;

        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot()),
            new Dictionary<string, object?> { ["script"] = "System.Environment.Exit(7);" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal(7, (int)result.Outputs["exit_code"]!);
        Assert.Contains("exited with code 7", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_WithArgs_PassesThroughToProgram()
    {
        if (!IsDotnetAvailable())
            return;

        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot()),
            new Dictionary<string, object?>
            {
                ["script"] = "Console.WriteLine(string.Join(\"|\", args));",
                ["args"] = new List<object?> { "alpha", "beta", "gamma" },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Contains("alpha|beta|gamma", (string)result.Outputs["stdout"]!);
    }

    [Fact]
    public async Task Execute_TempDirCleanedUpAfterRun()
    {
        if (!IsDotnetAvailable())
            return;

        var snapshot = Directory
            .EnumerateDirectories(Path.GetTempPath(), "creuser-csharp-*")
            .ToHashSet();

        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot()),
            new Dictionary<string, object?> { ["script"] = "Console.WriteLine(\"cleanup-test\");" },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Succeeded, result.Status);

        var after = Directory
            .EnumerateDirectories(Path.GetTempPath(), "creuser-csharp-*")
            .ToHashSet();
        // No new creuser-csharp-* directories should remain — finally clause cleans up.
        var leaked = after.Except(snapshot).ToList();
        Assert.Empty(leaked);
    }

    private static StepContext BuildContext(string workingTreePath)
    {
        return new StepContext(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "test-ws",
            WorkingTreePath: workingTreePath,
            StepId: Guid.NewGuid(),
            StepName: "csharp test step",
            Budgets: new StepBudgets(),
            Logger: NullLogger.Instance,
            AllowedCommands: null,
            RequiredSecrets: null,
            ResumeToken: null
        );
    }

    private static string WorkingTreeRoot()
    {
        // Distinct prefix from the runner's `creuser-csharp-*` temp dirs so
        // the cleanup test's glob doesn't pick these helpers up.
        var dir = Path.Combine(Path.GetTempPath(), $"creuser-csruntest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
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
