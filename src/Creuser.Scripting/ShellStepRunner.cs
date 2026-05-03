using System.Diagnostics;
using System.Text;
using Creuser.Core.Execution;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// Deterministic step runner for shell commands. Inputs:
/// <list type="bullet">
///   <item><c>script</c> — bash-style script body. For single-step jobs the script body becomes this automatically.</item>
///   <item><c>command</c> — alternate single-line command (used when <c>script</c> is absent). Either both fields can be set, with <c>script</c> taking precedence.</item>
///   <item><c>working_directory</c> — relative subdirectory inside the workspace working tree to <c>cd</c> into. Optional; defaults to the workspace root.</item>
/// </list>
///
/// <para>
/// Outputs: <c>{ exit_code: int, stdout: string, stderr: string }</c>. Stdout
/// and stderr are also captured as artifacts so the RunInspector can surface
/// the full output without bloating the step's outputs JSON.
/// </para>
///
/// <para>
/// <b>Allow-list enforcement.</b> The runner extracts every binary the
/// script references (the first token of each non-empty line, ignoring
/// shell builtins like <c>cd</c>, <c>echo</c>, <c>true</c>) and rejects
/// the step before execution if any binary is outside <see cref="StepContext.AllowedCommands"/>.
/// Jobs without a declared <c>allowed_commands</c> in their frontmatter
/// reject every shell step — operators must explicitly opt in to each
/// command. This is the architectural sandbox boundary.
/// </para>
///
/// <para>
/// File mutations during the script run are <em>not</em> currently captured
/// as <see cref="FileChange"/> records. The shell runner writes directly to
/// the working tree; the executor's transactional commit path lands in the
/// next slice (<c>file-mutate</c> + commit-on-step). For now the audit
/// record carries a count of zero file changes for shell steps; the actual
/// mutations are visible via git diff against the prior sync's commit SHA.
/// </para>
/// </summary>
public sealed class ShellStepRunner : IStepRunner
{
    public string StepType => "shell";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    // Bash builtins that don't need to be on the allow-list — they're not
    // separate binaries. Operators write `cd subdir && ./tool` without
    // having to declare `cd` as allowed.
    private static readonly HashSet<string> BashBuiltins = new(StringComparer.Ordinal)
    {
        "cd",
        "echo",
        "exit",
        "export",
        "true",
        "false",
        "set",
        "unset",
        "pwd",
        "test",
        "[",
        "[[",
        "if",
        "then",
        "else",
        "elif",
        "fi",
        "for",
        "while",
        "do",
        "done",
        "case",
        "esac",
        "return",
        "shift",
        "source",
        ".",
        ":",
    };

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        var script = GetString(inputs, "script") ?? GetString(inputs, "command");
        if (string.IsNullOrWhiteSpace(script))
        {
            sw.Stop();
            return StepResult.Failure(
                "shell step requires a `script` or `command` input. For single-step jobs the script body is moved into `script` automatically.",
                sw.ElapsedMilliseconds
            );
        }

        if (string.IsNullOrEmpty(ctx.WorkingTreePath))
        {
            sw.Stop();
            return StepResult.Failure(
                "shell step requires a workspace working tree. The workspace may be unsupported (s3) or its clone may not exist yet — sync the workspace and retry.",
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

        // Allow-list check. A null AllowedCommands means the operator didn't
        // declare any — block to be safe.
        var allowed = ctx.AllowedCommands;
        var referenced = ExtractCommandTokens(script);
        var unknown = referenced.Where(t => !BashBuiltins.Contains(t)).ToList();
        if (allowed is null)
        {
            sw.Stop();
            return StepResult.Failure(
                "shell step has no `allowed_commands` declared in frontmatter. Add the binaries this script needs to the allow-list (e.g. `allowed_commands: [git, rg, fd]`).",
                sw.ElapsedMilliseconds
            );
        }
        var notAllowed = unknown.Where(t => !allowed.Contains(t)).ToList();
        if (notAllowed.Count > 0)
        {
            sw.Stop();
            return StepResult.Failure(
                $"Command(s) not in allow-list: {string.Join(", ", notAllowed)}. Add to frontmatter `allowed_commands` to permit.",
                sw.ElapsedMilliseconds
            );
        }

        // Resolve working directory: workspace root + optional subdirectory.
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

        // Resolve timeout from budgets; fall back to 5min default.
        var timeout = ctx.Budgets.MaxDuration ?? DefaultTimeout;

        var env = ProcessRunner.StandardEnv(workingDir, ctx.RunId);

        var outcome = await ProcessRunner.RunAsync(
            fileName: "bash",
            arguments: ["-c", script],
            workingDirectory: workingDir,
            environment: env,
            timeout: timeout,
            ct: ct
        );

        sw.Stop();

        if (outcome.BinaryNotFound)
        {
            return StepResult.Failure(
                "bash binary not found on PATH. The shell runner requires `bash` on the host.",
                sw.ElapsedMilliseconds
            );
        }

        var exitCode = outcome.ExitCode;
        var stdoutStr = outcome.Stdout;
        var stderrStr = outcome.Stderr;

        if (outcome.TimedOut)
        {
            return new StepResult(
                Status: StepStatus.Failed,
                Outputs: new Dictionary<string, object?>
                {
                    ["exit_code"] = -1,
                    ["stdout"] = stdoutStr,
                    ["stderr"] = stderrStr,
                },
                FileChanges: Array.Empty<FileChange>(),
                Artifacts: BuildArtifacts(stdoutStr, stderrStr),
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"shell step timed out after {timeout.TotalSeconds:0}s."
            );
        }

        var ok = exitCode == 0;
        ctx.Logger.LogDebug(
            "shell step {StepName} exited {ExitCode} after {Ms}ms",
            ctx.StepName,
            exitCode,
            sw.ElapsedMilliseconds
        );
        return new StepResult(
            Status: ok ? StepStatus.Succeeded : StepStatus.Failed,
            Outputs: new Dictionary<string, object?>
            {
                ["exit_code"] = exitCode,
                ["stdout"] = stdoutStr,
                ["stderr"] = stderrStr,
            },
            FileChanges: Array.Empty<FileChange>(),
            Artifacts: BuildArtifacts(stdoutStr, stderrStr),
            DurationMs: sw.ElapsedMilliseconds,
            ErrorMessage: ok ? null : $"shell step exited with code {exitCode}."
        );
    }

    /// <summary>
    /// Pull out the first token of each non-empty, non-comment line of the
    /// script. Used for allow-list checking. Conservative — operators can
    /// always declare extra commands they don't actually use, but we won't
    /// let them slip in undeclared ones.
    /// </summary>
    internal static IReadOnlyList<string> ExtractCommandTokens(string script)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(script))
            return tokens;

        foreach (var rawLine in script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            // Split on the operators that join commands: `&&`, `||`, `;`, `|`.
            // Each segment is a separate command whose first word is the binary.
            var segments = SplitOnCommandJoiners(line);
            foreach (var segment in segments)
            {
                var s = segment.Trim();
                if (s.Length == 0)
                    continue;
                // Strip leading var assignments: `FOO=bar BAZ=qux cmd ...`
                while (true)
                {
                    var firstSpace = s.IndexOf(' ');
                    if (firstSpace < 0)
                        break;
                    var first = s[..firstSpace];
                    if (!IsAssignment(first))
                        break;
                    s = s[(firstSpace + 1)..].TrimStart();
                }
                if (s.Length == 0)
                    continue;
                var firstTokenEnd = FindWhitespaceOrParenIndex(s);
                var token = (firstTokenEnd < 0 ? s : s[..firstTokenEnd]).Trim();
                if (token.Length > 0 && !IsAssignment(token))
                    tokens.Add(token);
            }
        }

        return tokens.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> SplitOnCommandJoiners(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        var inSingle = false;
        var inDouble = false;
        while (i < line.Length)
        {
            var c = line[i];
            if (!inDouble && c == '\'')
                inSingle = !inSingle;
            else if (!inSingle && c == '"')
                inDouble = !inDouble;

            var nextC = i + 1 < line.Length ? line[i + 1] : '\0';
            if (!inSingle && !inDouble && (c == '&' && nextC == '&'))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2;
                continue;
            }
            if (!inSingle && !inDouble && (c == '|' && nextC == '|'))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2;
                continue;
            }
            if (!inSingle && !inDouble && (c == ';' || c == '|'))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 1;
                continue;
            }
            current.Append(c);
            i++;
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static bool IsAssignment(string token)
    {
        // FOO=bar — variable name = value, no spaces.
        var eq = token.IndexOf('=');
        if (eq <= 0)
            return false;
        for (var i = 0; i < eq; i++)
        {
            var c = token[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    private static int FindWhitespaceOrParenIndex(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]) || s[i] == '(' || s[i] == ')')
                return i;
        }
        return -1;
    }

    private static IReadOnlyList<StepArtifact> BuildArtifacts(string stdout, string stderr)
    {
        var arts = new List<StepArtifact>();
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

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;
}
