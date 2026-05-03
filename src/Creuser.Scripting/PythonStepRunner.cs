using System.Diagnostics;
using System.Text;
using Creuser.Core.Execution;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// Deterministic step runner for Python scripts via <c>uv run</c>. Inputs:
/// <list type="bullet">
///   <item><c>script</c> — Python source body. For single-step jobs the body of the script becomes this automatically.</item>
///   <item><c>args</c> — optional list of strings passed to the script.</item>
///   <item><c>working_directory</c> — relative subdirectory inside the workspace working tree.</item>
/// </list>
///
/// <para>
/// <c>uv</c> is the runner of choice (over plain <c>python3</c>) because it
/// honors <a href="https://peps.python.org/pep-0723/">PEP 723</a> inline
/// dependencies: a script can declare its packages in a header comment
/// (<c># /// script\n# dependencies = ["requests"]\n# ///</c>) and
/// <c>uv run</c> resolves them transparently. Without a header, <c>uv run</c>
/// behaves equivalently to <c>python3</c>. The <c>:latest</c> fat image
/// includes <c>uv</c>; the <c>:slim</c> image does not.
/// </para>
///
/// <para>
/// Same security boundary as <c>csharp</c>: the runner can do anything
/// Python can, so the per-binary allow-list doesn't apply. Process-level
/// constraints — restricted env, bounded timeout, post-v1 UID separation —
/// are the safety net.
/// </para>
/// </summary>
public sealed class PythonStepRunner : IStepRunner
{
    public string StepType => "python";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        var script = GetString(inputs, "script");
        if (string.IsNullOrWhiteSpace(script))
        {
            sw.Stop();
            return StepResult.Failure(
                "python step requires a `script` input. For single-step jobs the script body is moved into `script` automatically.",
                sw.ElapsedMilliseconds
            );
        }

        if (string.IsNullOrEmpty(ctx.WorkingTreePath) || !Directory.Exists(ctx.WorkingTreePath))
        {
            sw.Stop();
            return StepResult.Failure(
                "python step requires a workspace working tree. Sync the workspace first, or verify the local path is configured.",
                sw.ElapsedMilliseconds
            );
        }

        var workingDir = ctx.WorkingTreePath;
        var subDir = GetString(inputs, "working_directory");
        if (!string.IsNullOrWhiteSpace(subDir))
        {
            var combined = Path.GetFullPath(Path.Combine(ctx.WorkingTreePath, subDir));
            if (!combined.StartsWith(ctx.WorkingTreePath, StringComparison.Ordinal))
            {
                sw.Stop();
                return StepResult.Failure(
                    $"working_directory '{subDir}' escapes the workspace root.",
                    sw.ElapsedMilliseconds
                );
            }
            if (!Directory.Exists(combined))
            {
                sw.Stop();
                return StepResult.Failure(
                    $"working_directory '{subDir}' does not exist inside the working tree.",
                    sw.ElapsedMilliseconds
                );
            }
            workingDir = combined;
        }

        var scriptArgs = ExtractArgs(inputs);
        var timeout = ctx.Budgets.MaxDuration ?? DefaultTimeout;

        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"creuser-python-{ctx.RunId:N}-{ctx.StepId:N}"
        );
        try
        {
            Directory.CreateDirectory(tmpDir);
            var scriptPath = Path.Combine(tmpDir, "script.py");
            await File.WriteAllTextAsync(scriptPath, script, ct);

            var args = new List<string> { "run", scriptPath };
            args.AddRange(scriptArgs);

            // uv needs HOME to be set so it can find / create its cache;
            // ProcessRunner.StandardEnv supplies that.
            var env = ProcessRunner.StandardEnv(workingDir, ctx.RunId);

            var outcome = await ProcessRunner.RunAsync(
                fileName: "uv",
                arguments: args,
                workingDirectory: workingDir,
                environment: env,
                timeout: timeout,
                ct: ct
            );

            sw.Stop();

            if (outcome.BinaryNotFound)
            {
                return StepResult.Failure(
                    "uv binary not found on PATH. The python runner uses `uv run` (PEP 723 inline deps). The :latest fat image includes uv; for :slim, install uv (https://docs.astral.sh/uv/) or use a `shell` step that calls `python3` directly.",
                    sw.ElapsedMilliseconds
                );
            }

            return BuildResult(
                ctx,
                script,
                outcome,
                timeout,
                sw.ElapsedMilliseconds,
                runnerName: "python"
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(tmpDir))
                    Directory.Delete(tmpDir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    internal static StepResult BuildResult(
        StepContext ctx,
        string script,
        ProcessOutcome outcome,
        TimeSpan timeout,
        long durationMs,
        string runnerName
    )
    {
        var artifacts = BuildArtifacts(script, outcome.Stdout, outcome.Stderr, runnerName);
        if (outcome.TimedOut)
        {
            return new StepResult(
                Status: StepStatus.Failed,
                Outputs: new Dictionary<string, object?>
                {
                    ["exit_code"] = -1,
                    ["stdout"] = outcome.Stdout,
                    ["stderr"] = outcome.Stderr,
                },
                FileChanges: Array.Empty<FileChange>(),
                Artifacts: artifacts,
                DurationMs: durationMs,
                ErrorMessage: $"{runnerName} step timed out after {timeout.TotalSeconds:0}s."
            );
        }

        var ok = outcome.ExitCode == 0;
        ctx.Logger.LogDebug(
            "{Runner} step {StepName} exited {ExitCode} after {Ms}ms",
            runnerName,
            ctx.StepName,
            outcome.ExitCode,
            durationMs
        );
        return new StepResult(
            Status: ok ? StepStatus.Succeeded : StepStatus.Failed,
            Outputs: new Dictionary<string, object?>
            {
                ["exit_code"] = outcome.ExitCode,
                ["stdout"] = outcome.Stdout,
                ["stderr"] = outcome.Stderr,
            },
            FileChanges: Array.Empty<FileChange>(),
            Artifacts: artifacts,
            DurationMs: durationMs,
            ErrorMessage: ok ? null : $"{runnerName} step exited with code {outcome.ExitCode}."
        );
    }

    internal static IReadOnlyList<StepArtifact> BuildArtifacts(
        string script,
        string stdout,
        string stderr,
        string runnerName
    )
    {
        var (sourceName, contentType) = runnerName switch
        {
            "python" => ("script.py", "text/x-python"),
            "node" => ("script.js", "text/javascript"),
            _ => ("script.txt", "text/plain"),
        };
        var arts = new List<StepArtifact>
        {
            new("source", sourceName, Encoding.UTF8.GetBytes(script), contentType),
        };
        if (stdout.Length > 0)
            arts.Add(
                new StepArtifact(
                    "stdout",
                    "stdout.txt",
                    Encoding.UTF8.GetBytes(stdout),
                    "text/plain"
                )
            );
        if (stderr.Length > 0)
            arts.Add(
                new StepArtifact(
                    "stderr",
                    "stderr.txt",
                    Encoding.UTF8.GetBytes(stderr),
                    "text/plain"
                )
            );
        return arts;
    }

    internal static List<string> ExtractArgs(IReadOnlyDictionary<string, object?> inputs)
    {
        if (!inputs.TryGetValue("args", out var raw) || raw is null)
            return new List<string>();
        if (raw is string single)
            return new List<string> { single };
        if (raw is IEnumerable<object?> seq)
            return seq.Where(v => v is not null).Select(v => v!.ToString()!).ToList();
        return new List<string> { raw.ToString()! };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;
}
