using System.Diagnostics;
using System.Text;
using Creuser.Core.Execution;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// Deterministic step runner for single-file C# scripts via .NET 10
/// file-based apps (<c>dotnet run script.cs</c>). Inputs:
/// <list type="bullet">
///   <item><c>script</c> — the C# source body. For single-step jobs the body of the script becomes this automatically.</item>
///   <item><c>args</c> — optional list of strings passed to the program as command-line arguments.</item>
///   <item><c>working_directory</c> — relative subdirectory inside the workspace working tree. Defaults to the workspace root.</item>
/// </list>
///
/// <para>
/// Outputs: <c>{ exit_code: int, stdout: string, stderr: string }</c> +
/// stdout/stderr captured as artifacts. The script source itself is also
/// captured as an artifact so the run is reproducible.
/// </para>
///
/// <para>
/// <b>Allow-list semantics differ from <c>shell</c>.</b> A C# script can
/// invoke arbitrary .NET APIs and spawn subprocesses, so a per-binary
/// allow-list doesn't bound what it can do. The security boundary is
/// process-level: restricted env (no inherited host secrets), bounded
/// timeout (5 min default, override via budgets), and — post-v1 — UID
/// separation + ulimits. v0.1's threat model is single-tenant on-prem
/// where the operator is trusted; future multi-tenant deployments land
/// real sandboxing (Firecracker / gVisor).
/// </para>
///
/// <para>
/// The script source is materialized to a per-run temp directory under
/// <c>${TMPDIR}/creuser-csharp-&lt;runId&gt;-&lt;stepId&gt;/script.cs</c>,
/// so .NET's adjacent <c>obj/</c> + <c>bin/</c> caches don't pollute the
/// workspace working tree. The temp directory is deleted in finally —
/// best-effort cleanup; if it survives a crash, a periodic tmp sweeper
/// will reclaim it.
/// </para>
/// </summary>
public sealed class CSharpStepRunner : IStepRunner
{
    public string StepType => "csharp";

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
                "csharp step requires a `script` input. For single-step jobs the script body is moved into `script` automatically.",
                sw.ElapsedMilliseconds
            );
        }

        if (string.IsNullOrEmpty(ctx.WorkingTreePath))
        {
            sw.Stop();
            return StepResult.Failure(
                "csharp step requires a workspace working tree. The workspace may be unsupported (s3) or its clone may not exist yet — sync the workspace and retry.",
                sw.ElapsedMilliseconds
            );
        }
        if (!Directory.Exists(ctx.WorkingTreePath))
        {
            sw.Stop();
            return StepResult.Failure(
                $"Working tree does not exist on disk: {ctx.WorkingTreePath}. For git workspaces, sync first; for local workspaces, verify the configured path.",
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
            $"creuser-csharp-{ctx.RunId:N}-{ctx.StepId:N}"
        );
        try
        {
            Directory.CreateDirectory(tmpDir);
            var scriptPath = Path.Combine(tmpDir, "script.cs");
            await File.WriteAllTextAsync(scriptPath, script, ct);

            var args = new List<string> { "run", scriptPath };
            // `--` separates dotnet args from script args. Always emit the
            // separator even when scriptArgs is empty so future additions
            // don't accidentally collide.
            args.Add("--");
            args.AddRange(scriptArgs);

            var env = ProcessRunner.StandardEnv(workingDir, ctx.RunId);

            var outcome = await ProcessRunner.RunAsync(
                fileName: "dotnet",
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
                    "dotnet binary not found on PATH. The csharp runner requires the .NET 10 SDK. The :latest fat image includes it; the :slim image does not.",
                    sw.ElapsedMilliseconds
                );
            }

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
                    Artifacts: BuildArtifacts(script, outcome.Stdout, outcome.Stderr),
                    DurationMs: sw.ElapsedMilliseconds,
                    ErrorMessage: $"csharp step timed out after {timeout.TotalSeconds:0}s."
                );
            }

            var ok = outcome.ExitCode == 0;
            ctx.Logger.LogDebug(
                "csharp step {StepName} exited {ExitCode} after {Ms}ms",
                ctx.StepName,
                outcome.ExitCode,
                sw.ElapsedMilliseconds
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
                Artifacts: BuildArtifacts(script, outcome.Stdout, outcome.Stderr),
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: ok ? null : $"csharp step exited with code {outcome.ExitCode}."
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
                // best-effort; tmp sweeper will reclaim if anything's left.
            }
        }
    }

    private static IReadOnlyList<StepArtifact> BuildArtifacts(
        string script,
        string stdout,
        string stderr
    )
    {
        var arts = new List<StepArtifact>
        {
            // Persist the script source so the run is reproducible without
            // having to fetch the script body separately. Future replay can
            // diff against the script row to detect drift.
            new("source", "script.cs", Encoding.UTF8.GetBytes(script), "text/x-csharp"),
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

    private static List<string> ExtractArgs(IReadOnlyDictionary<string, object?> inputs)
    {
        if (!inputs.TryGetValue("args", out var raw) || raw is null)
            return new List<string>();

        // YAML round-trips arrays as List<object>; tolerate strings too
        // (single-arg shorthand) and IEnumerable in general.
        if (raw is string single)
            return new List<string> { single };
        if (raw is IEnumerable<object?> seq)
            return seq.Where(v => v is not null).Select(v => v!.ToString()!).ToList();
        return new List<string> { raw.ToString()! };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;
}
