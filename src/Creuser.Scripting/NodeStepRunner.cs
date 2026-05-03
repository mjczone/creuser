using System.Diagnostics;
using Creuser.Core.Execution;

namespace Creuser.Scripting;

/// <summary>
/// Deterministic step runner for JavaScript scripts via <c>node</c>. Inputs:
/// <list type="bullet">
///   <item><c>script</c> — JavaScript source body. For single-step jobs the body becomes this automatically.</item>
///   <item><c>args</c> — optional list of strings passed to the script.</item>
///   <item><c>working_directory</c> — relative subdirectory inside the workspace working tree.</item>
/// </list>
///
/// <para>
/// v0.1 runs scripts as bare <c>node script.js</c> with no automatic
/// dependency resolution. Operators who need npm packages drop a
/// <c>package.json</c> + <c>node_modules/</c> in the workspace and the
/// script can <c>require</c> them — node's resolution algorithm walks up
/// from the script's location to the working tree, which is the cwd.
/// Future: a <c>--deps</c> input that auto-installs scoped packages via
/// <c>npx</c> (deferred — `shell` job can do this today).
/// </para>
///
/// <para>
/// TypeScript via <c>tsx</c> / <c>ts-node</c> is not in v0.1 — operators
/// can call <c>npx --yes tsx</c> from a <c>shell</c> step. The dedicated
/// TypeScript runner lands when the type-checked-script use case justifies
/// the extra dependency.
/// </para>
/// </summary>
public sealed class NodeStepRunner : IStepRunner
{
    public string StepType => "node";

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
                "node step requires a `script` input. For single-step jobs the script body is moved into `script` automatically.",
                sw.ElapsedMilliseconds
            );
        }

        if (string.IsNullOrEmpty(ctx.WorkingTreePath) || !Directory.Exists(ctx.WorkingTreePath))
        {
            sw.Stop();
            return StepResult.Failure(
                "node step requires a workspace working tree. Sync the workspace first, or verify the local path is configured.",
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

        var scriptArgs = PythonStepRunner.ExtractArgs(inputs);
        var timeout = ctx.Budgets.MaxDuration ?? DefaultTimeout;

        var tmpDir = Path.Combine(Path.GetTempPath(), $"creuser-node-{ctx.RunId:N}-{ctx.StepId:N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var scriptPath = Path.Combine(tmpDir, "script.js");
            await File.WriteAllTextAsync(scriptPath, script, ct);

            var args = new List<string> { scriptPath };
            args.AddRange(scriptArgs);

            var env = ProcessRunner.StandardEnv(workingDir, ctx.RunId);

            var outcome = await ProcessRunner.RunAsync(
                fileName: "node",
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
                    "node binary not found on PATH. The node runner requires Node.js. The :latest fat image includes Node 24 LTS; the :slim image does not.",
                    sw.ElapsedMilliseconds
                );
            }

            return PythonStepRunner.BuildResult(
                ctx,
                script,
                outcome,
                timeout,
                sw.ElapsedMilliseconds,
                runnerName: "node"
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

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;
}
