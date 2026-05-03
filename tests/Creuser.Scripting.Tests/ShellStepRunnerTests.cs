using Creuser.Core.Execution;
using Creuser.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

public class ShellStepRunnerTests
{
    private static readonly ShellStepRunner Runner = new();

    [Fact]
    public void StepType_IsShell()
    {
        Assert.Equal("shell", Runner.StepType);
    }

    [Fact]
    public async Task Execute_NoScriptOrCommand_FailsWithMessage()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot()),
            new Dictionary<string, object?>(),
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("`script` or `command`", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_NoAllowList_RejectsBeforeRunning()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot(), allowed: null),
            new Dictionary<string, object?> { ["script"] = "echo hello" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("allowed_commands", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_CommandNotInAllowList_RejectsBeforeRunning()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot(), allowed: new[] { "echo" }),
            new Dictionary<string, object?> { ["script"] = "rm -rf /" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("not in allow-list", result.ErrorMessage);
        Assert.Contains("rm", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_BashBuiltinsAllowedWithoutDeclaration()
    {
        // `cd` and `echo` are bash builtins; they don't need to be on the
        // allow-list. The script runs without any user-declared binaries.
        // (This intentionally uses /bin/echo which is technically external,
        // but `echo` is also a bash builtin so the parser sees it as a builtin.)
        if (!IsBashAvailable())
            return;
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot(), allowed: Array.Empty<string>()),
            new Dictionary<string, object?> { ["script"] = "echo hi" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(0, (int)result.Outputs["exit_code"]!);
        Assert.Contains("hi", (string)result.Outputs["stdout"]!);
    }

    [Fact]
    public async Task Execute_StdoutAndStderrCaptured()
    {
        if (!IsBashAvailable())
            return;
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot(), allowed: Array.Empty<string>()),
            new Dictionary<string, object?>
            {
                ["script"] = "echo to-stdout && echo to-stderr 1>&2",
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Contains("to-stdout", (string)result.Outputs["stdout"]!);
        Assert.Contains("to-stderr", (string)result.Outputs["stderr"]!);
    }

    [Fact]
    public async Task Execute_NonZeroExit_MarksStepFailed()
    {
        if (!IsBashAvailable())
            return;
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot(), allowed: Array.Empty<string>()),
            new Dictionary<string, object?> { ["script"] = "exit 7" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal(7, (int)result.Outputs["exit_code"]!);
        Assert.Contains("exited with code 7", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_MissingWorkingTree_FailsWithClearMessage()
    {
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: "/nonexistent/path/abc123"),
            new Dictionary<string, object?> { ["script"] = "echo hi" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("does not exist on disk", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_WorkingDirectoryEscapesRoot_Refused()
    {
        // Allow-list populated so the validation order proceeds past
        // allow-list and reaches the working_directory check.
        var result = await Runner.ExecuteAsync(
            BuildContext(workingTreePath: WorkingTreeRoot(), allowed: Array.Empty<string>()),
            new Dictionary<string, object?>
            {
                ["script"] = "echo x",
                ["working_directory"] = "../../etc",
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("escapes the workspace root", result.ErrorMessage);
    }

    [Theory]
    [InlineData("echo hi", new[] { "echo" })]
    [InlineData("rm -rf foo", new[] { "rm" })]
    [InlineData("git status && echo done", new[] { "git", "echo" })]
    [InlineData("FOO=bar git push", new[] { "git" })]
    [InlineData("# just a comment", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("cat foo | jq .name", new[] { "cat", "jq" })]
    [InlineData("ls; pwd; whoami", new[] { "ls", "pwd", "whoami" })]
    public void ExtractCommandTokens_ParsesScriptCorrectly(string script, string[] expected)
    {
        var tokens = ShellStepRunner.ExtractCommandTokens(script);
        Assert.Equal(expected, tokens);
    }

    [Fact]
    public void ExtractCommandTokens_ConditionalsTokenizedAsBuiltins()
    {
        // Conservative tokenization: `if`, `then`, `fi` are seen as the
        // first tokens of their respective sub-commands (semicolon split).
        // They're all bash builtins so the allow-list check skips them.
        // A future tree-sitter-based shell parser could see "into" the
        // conditional to surface `[` and the command after `then` — for
        // v0.1 the conservative parser plus builtin-skip is sufficient.
        var tokens = ShellStepRunner.ExtractCommandTokens("if [ -f foo ]; then cat foo; fi");
        Assert.Contains("if", tokens);
        Assert.Contains("then", tokens);
        Assert.Contains("fi", tokens);
    }

    private static StepContext BuildContext(
        string workingTreePath,
        IReadOnlyCollection<string>? allowed = null
    )
    {
        IReadOnlySet<string>? set = allowed is null
            ? null
            : new HashSet<string>(allowed, StringComparer.Ordinal);
        return new StepContext(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "test-ws",
            WorkingTreePath: workingTreePath,
            StepId: Guid.NewGuid(),
            StepName: "test step",
            Budgets: new StepBudgets(),
            Logger: NullLogger.Instance,
            AllowedCommands: set,
            RequiredSecrets: null,
            ResumeToken: null
        );
    }

    private static string WorkingTreeRoot()
    {
        // Use a freshly-created temp directory so each test gets its own
        // working tree to play in. Cleaned up at process exit by the OS.
        var dir = Path.Combine(Path.GetTempPath(), $"creuser-shell-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
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
