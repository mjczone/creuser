using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Creuser.Core.Execution;
using Microsoft.Extensions.AI;

namespace Creuser.Scripting.ToolLoop;

/// <summary>
/// Default <see cref="IToolLoopToolRegistry"/>: a curated set of read-only
/// workspace tools. Each tool is bounded (size caps, match caps) so a chatty
/// model can't blow the token budget on a single response, and each is
/// path-guarded so the model can't read above the working tree.
///
/// <para>
/// All five tools return JSON-shaped objects. On failure each returns
/// <c>{ ok: false, error: "..." }</c>; the model gets to recover. Path
/// escapes are the only errors marked <c>fatal: true</c> — the runner
/// aborts the loop instead of letting the model retry.
/// </para>
/// </summary>
public sealed class WorkspaceToolLoopRegistry : IToolLoopToolRegistry
{
    public static IReadOnlyList<string> ToolNames { get; } =
        new[]
        {
            "read_file",
            "list_directory",
            "grep",
            "find_files_by_pattern",
            "git_log",
            "write_file",
            "delete_file",
        };

    public IReadOnlyList<string> AvailableTools => ToolNames;

    public IReadOnlyList<AIFunction> BuildTools(
        IReadOnlyList<string> names,
        StepContext ctx,
        ToolLogSink sink
    )
    {
        var rootPath = ctx.WorkingTreePath;
        if (string.IsNullOrEmpty(rootPath))
            throw new ToolLoopException(
                "llm-tool-loop step requires a workspace working tree. The workspace may be unsupported (s3) or its clone may not exist yet — sync the workspace and retry."
            );

        var built = new List<AIFunction>(names.Count);
        foreach (var name in names)
        {
            AIFunction tool = name switch
            {
                "read_file" => BuildReadFile(rootPath, sink),
                "list_directory" => BuildListDirectory(rootPath, sink),
                "grep" => BuildGrep(rootPath, sink),
                "find_files_by_pattern" => BuildFindFilesByPattern(rootPath, sink),
                "git_log" => BuildGitLog(rootPath, sink),
                "write_file" => BuildWriteFile(rootPath, sink),
                "delete_file" => BuildDeleteFile(rootPath, sink),
                _ => throw new ToolLoopException(
                    $"Unknown tool '{name}'. Available tools: {string.Join(", ", AvailableTools)}."
                ),
            };
            built.Add(tool);
        }
        return built;
    }

    private static AIFunction BuildReadFile(string root, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            (
                [Description("Path to the file relative to the workspace root. Forward slashes.")]
                    string path,
                [Description(
                    "Maximum bytes to return. Defaults to 65536. The result signals when the file was truncated."
                )]
                    int? max_bytes = null
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { path, max_bytes });
                var cap = max_bytes is int n && n > 0 ? n : 65536;
                try
                {
                    if (!PathGuard.TryResolveSafe(root, path, out var full, out var err))
                        return RecordFatal(sink, "read_file", argsJson, err, sw);
                    if (!File.Exists(full))
                    {
                        var res = new { ok = false, error = $"File not found: {path}" };
                        return RecordResult(sink, "read_file", argsJson, res, sw);
                    }
                    var info = new FileInfo(full);
                    var size = info.Length;
                    using var stream = File.OpenRead(full);
                    var buffer = new byte[Math.Min(cap, (int)Math.Min(size, int.MaxValue))];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    var content = Encoding.UTF8.GetString(buffer, 0, read);
                    var truncated = size > read;
                    var result = new
                    {
                        ok = true,
                        path,
                        content,
                        truncated,
                        size,
                    };
                    return RecordResult(sink, "read_file", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "read_file", argsJson, ex, sw);
                }
            },
            name: "read_file",
            description: "Read a file from the workspace working tree. Returns content, size, and truncated flag. Caps the read at max_bytes (default 65536)."
        );

    private static AIFunction BuildListDirectory(string root, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            (
                [Description(
                    "Directory path relative to the workspace root. Use '.' for the workspace root itself."
                )]
                    string? path = null,
                [Description(
                    "When true, walks subdirectories. Default false (immediate children only)."
                )]
                    bool? recursive = null,
                [Description(
                    "Cap on entries returned. Default 200. The result signals truncation."
                )]
                    int? max_entries = null
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(
                    new
                    {
                        path,
                        recursive,
                        max_entries,
                    }
                );
                var dir = path ?? ".";
                var cap = max_entries is int n && n > 0 ? n : 200;
                var rec = recursive ?? false;
                try
                {
                    if (!PathGuard.TryResolveSafe(root, dir, out var full, out var err))
                        return RecordFatal(sink, "list_directory", argsJson, err, sw);
                    if (!Directory.Exists(full))
                    {
                        var res = new { ok = false, error = $"Directory not found: {dir}" };
                        return RecordResult(sink, "list_directory", argsJson, res, sw);
                    }
                    var option = rec ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var entries = new List<object>();
                    var truncated = false;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", option))
                    {
                        if (entries.Count >= cap)
                        {
                            truncated = true;
                            break;
                        }
                        var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                        var isDir = Directory.Exists(entry);
                        long? esize = null;
                        if (!isDir)
                        {
                            try
                            {
                                esize = new FileInfo(entry).Length;
                            }
                            catch
                            {
                                // best effort
                            }
                        }
                        entries.Add(
                            new
                            {
                                name = rel,
                                kind = isDir ? "directory" : "file",
                                size = esize,
                            }
                        );
                    }
                    var result = new
                    {
                        ok = true,
                        path = dir,
                        entries,
                        truncated,
                    };
                    return RecordResult(sink, "list_directory", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "list_directory", argsJson, ex, sw);
                }
            },
            name: "list_directory",
            description: "List entries inside a workspace directory. Optionally recursive; capped at max_entries."
        );

    private static AIFunction BuildGrep(string root, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (
                [Description(
                    "Regex pattern (POSIX extended). Anchor with ^ / $ as needed. Pass simple substrings for non-regex matching."
                )]
                    string pattern,
                [Description(
                    "Directory to search relative to the workspace root. Defaults to '.'."
                )]
                    string? path = null,
                [Description(
                    "Optional file-name glob to restrict the search (e.g. '*.cs'). Translates to grep --include."
                )]
                    string? file_glob = null,
                [Description("Cap on matches returned. Default 50. Result signals truncation.")]
                    int? max_matches = null,
                CancellationToken ct = default
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(
                    new
                    {
                        pattern,
                        path,
                        file_glob,
                        max_matches,
                    }
                );
                var dir = path ?? ".";
                var cap = max_matches is int n && n > 0 ? n : 50;
                try
                {
                    if (!PathGuard.TryResolveSafe(root, dir, out var full, out var err))
                        return RecordFatal(sink, "grep", argsJson, err, sw);
                    if (!Directory.Exists(full) && !File.Exists(full))
                    {
                        var res = new { ok = false, error = $"Search target not found: {dir}" };
                        return RecordResult(sink, "grep", argsJson, res, sw);
                    }

                    var args = new List<string> { "-E", "-rn", "--color=never" };
                    if (!string.IsNullOrWhiteSpace(file_glob))
                        args.Add($"--include={file_glob}");
                    args.Add(pattern);
                    args.Add(full);

                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        ct,
                        timeout.Token
                    );
                    var outcome = await ProcessRunner.RunAsync(
                        "grep",
                        args,
                        workingDirectory: root,
                        environment: ProcessRunner.StandardEnv(root, Guid.Empty),
                        timeout: TimeSpan.FromSeconds(15),
                        linked.Token
                    );
                    if (outcome.BinaryNotFound)
                    {
                        var res = new
                        {
                            ok = false,
                            error = "grep binary not found on PATH in this deployment.",
                        };
                        return RecordResult(sink, "grep", argsJson, res, sw);
                    }
                    // grep exit codes: 0 match, 1 no match, 2 error.
                    if (outcome.ExitCode == 1)
                    {
                        var res = new
                        {
                            ok = true,
                            matches = Array.Empty<object>(),
                            truncated = false,
                        };
                        return RecordResult(sink, "grep", argsJson, res, sw);
                    }
                    if (outcome.ExitCode != 0)
                    {
                        var res = new
                        {
                            ok = false,
                            error = $"grep failed (exit {outcome.ExitCode}): "
                                + outcome.Stderr.Trim(),
                        };
                        return RecordResult(sink, "grep", argsJson, res, sw);
                    }

                    var matches = new List<object>();
                    var truncated = false;
                    foreach (
                        var line in outcome.Stdout.Split(
                            '\n',
                            StringSplitOptions.RemoveEmptyEntries
                        )
                    )
                    {
                        if (matches.Count >= cap)
                        {
                            truncated = true;
                            break;
                        }
                        var parsed = ParseGrepLine(line, root);
                        if (parsed is not null)
                            matches.Add(parsed);
                    }

                    var result = new
                    {
                        ok = true,
                        matches,
                        truncated,
                    };
                    return RecordResult(sink, "grep", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "grep", argsJson, ex, sw);
                }
            },
            name: "grep",
            description: "Search the working tree for a regex pattern, returning {file, line, text} matches. Honors file_glob; capped at max_matches."
        );

    private static AIFunction BuildFindFilesByPattern(string root, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            (
                [Description(
                    "Glob pattern relative to the workspace root, e.g. 'src/**/*.cs', 'docs/*.md'. Forward slashes."
                )]
                    string glob,
                [Description("Cap on results returned. Default 200.")] int? max_results = null
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { glob, max_results });
                var cap = max_results is int n && n > 0 ? n : 200;
                try
                {
                    var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher(
                        StringComparison.OrdinalIgnoreCase
                    );
                    matcher.AddInclude(glob);
                    var dirInfo =
                        new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
                            new DirectoryInfo(root)
                        );
                    var match = matcher.Execute(dirInfo);
                    var files = new List<string>();
                    var truncated = false;
                    foreach (var f in match.Files)
                    {
                        if (files.Count >= cap)
                        {
                            truncated = true;
                            break;
                        }
                        files.Add(f.Path.Replace('\\', '/'));
                    }
                    var result = new
                    {
                        ok = true,
                        files,
                        truncated,
                    };
                    return RecordResult(sink, "find_files_by_pattern", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "find_files_by_pattern", argsJson, ex, sw);
                }
            },
            name: "find_files_by_pattern",
            description: "Find files in the working tree matching a glob pattern (e.g. 'src/**/*.cs'). Returns relative paths."
        );

    private static AIFunction BuildGitLog(string root, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            async (
                [Description(
                    "Optional path to scope the log to a file or directory. Omit for repo-wide log."
                )]
                    string? path = null,
                [Description("Cap on commits returned. Default 20.")] int? limit = null,
                CancellationToken ct = default
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { path, limit });
                var cap = limit is int n && n > 0 ? n : 20;
                try
                {
                    if (
                        !string.IsNullOrEmpty(path)
                        && !PathGuard.TryResolveSafe(root, path, out _, out var err)
                    )
                        return RecordFatal(sink, "git_log", argsJson, err, sw);

                    if (!Directory.Exists(Path.Combine(root, ".git")))
                    {
                        var res = new
                        {
                            ok = false,
                            error = "Working tree is not a git repository (no .git directory).",
                        };
                        return RecordResult(sink, "git_log", argsJson, res, sw);
                    }

                    var args = new List<string>
                    {
                        "log",
                        $"--max-count={cap}",
                        "--pretty=format:%H%x1f%an%x1f%aI%x1f%s",
                    };
                    if (!string.IsNullOrEmpty(path))
                    {
                        args.Add("--");
                        args.Add(path);
                    }

                    var outcome = await ProcessRunner.RunAsync(
                        "git",
                        args,
                        workingDirectory: root,
                        environment: ProcessRunner.StandardEnv(root, Guid.Empty),
                        timeout: TimeSpan.FromSeconds(15),
                        ct
                    );
                    if (outcome.BinaryNotFound)
                    {
                        var res = new
                        {
                            ok = false,
                            error = "git binary not found on PATH in this deployment.",
                        };
                        return RecordResult(sink, "git_log", argsJson, res, sw);
                    }
                    if (outcome.ExitCode != 0)
                    {
                        var res = new
                        {
                            ok = false,
                            error = $"git log failed (exit {outcome.ExitCode}): "
                                + outcome.Stderr.Trim(),
                        };
                        return RecordResult(sink, "git_log", argsJson, res, sw);
                    }
                    var commits = new List<object>();
                    foreach (
                        var line in outcome.Stdout.Split(
                            '\n',
                            StringSplitOptions.RemoveEmptyEntries
                        )
                    )
                    {
                        var parts = line.Split('\x1f');
                        if (parts.Length < 4)
                            continue;
                        commits.Add(
                            new
                            {
                                sha = parts[0],
                                author = parts[1],
                                when = parts[2],
                                subject = parts[3],
                            }
                        );
                    }
                    var result = new { ok = true, commits };
                    return RecordResult(sink, "git_log", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "git_log", argsJson, ex, sw);
                }
            },
            name: "git_log",
            description: "Read git history for the working tree. Optionally scoped to a path. Returns commits as {sha, author, when, subject}."
        );

    private static AIFunction BuildWriteFile(string root, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            (
                [Description(
                    "Path to the file relative to the workspace root. Forward slashes. Missing parent directories are created automatically."
                )]
                    string path,
                [Description("UTF-8 file content. Replaces the file if it exists.")] string content
            ) =>
            {
                var sw = Stopwatch.StartNew();
                // Don't echo content into the tool log — file writes can carry
                // sizable bodies. Just record the path + size.
                var argsJson = JsonSerializer.Serialize(
                    new { path, content_length = content?.Length ?? 0 }
                );
                try
                {
                    if (!PathGuard.TryResolveSafe(root, path, out var full, out var err))
                        return RecordFatal(sink, "write_file", argsJson, err, sw);
                    if (path.Contains(".git/", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = new { ok = false, error = "Refusing to write inside .git/." };
                        return RecordResult(sink, "write_file", argsJson, res, sw);
                    }
                    var dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(full, content ?? string.Empty, Encoding.UTF8);
                    var size = new FileInfo(full).Length;
                    var result = new
                    {
                        ok = true,
                        path,
                        size,
                    };
                    return RecordResult(sink, "write_file", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "write_file", argsJson, ex, sw);
                }
            },
            name: "write_file",
            description: "Write (or overwrite) a UTF-8 text file in the workspace working tree. Creates parent directories as needed. Use ONLY when the user has explicitly asked you to make changes — read tools first, then write. Returns the path and final size."
        );

    private static AIFunction BuildDeleteFile(string root, ToolLogSink sink) =>
        AIFunctionFactory.Create(
            (
                [Description("Path to the file relative to the workspace root. Forward slashes.")]
                    string path
            ) =>
            {
                var sw = Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { path });
                try
                {
                    if (!PathGuard.TryResolveSafe(root, path, out var full, out var err))
                        return RecordFatal(sink, "delete_file", argsJson, err, sw);
                    if (path.Contains(".git/", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = new { ok = false, error = "Refusing to delete inside .git/." };
                        return RecordResult(sink, "delete_file", argsJson, res, sw);
                    }
                    if (Directory.Exists(full))
                    {
                        var res = new
                        {
                            ok = false,
                            error = $"Path is a directory, not a file: {path}",
                        };
                        return RecordResult(sink, "delete_file", argsJson, res, sw);
                    }
                    var existed = File.Exists(full);
                    if (existed)
                        File.Delete(full);
                    var result = new
                    {
                        ok = true,
                        path,
                        existed,
                    };
                    return RecordResult(sink, "delete_file", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordError(sink, "delete_file", argsJson, ex, sw);
                }
            },
            name: "delete_file",
            description: "Delete a file from the workspace working tree. Idempotent — succeeds with `existed: false` when the file is already gone. Use ONLY when the user has explicitly asked to remove the file. Refuses to delete directories (use the file manager UI for that)."
        );

    private static object ParseGrepLine(string line, string root)
    {
        // grep -rn output: "<absolute-path>:<line>:<text>"
        var firstColon = line.IndexOf(':');
        if (firstColon <= 0)
            return null!;
        var secondColon = line.IndexOf(':', firstColon + 1);
        if (secondColon <= 0)
            return null!;
        var fileAbs = line[..firstColon];
        var lineNumStr = line[(firstColon + 1)..secondColon];
        var text = line[(secondColon + 1)..];
        if (!int.TryParse(lineNumStr, out var lineNum))
            return null!;
        var rel = Path.GetRelativePath(root, fileAbs).Replace('\\', '/');
        return new
        {
            file = rel,
            line = lineNum,
            text,
        };
    }

    private static object RecordResult(
        ToolLogSink sink,
        string tool,
        string argsJson,
        object result,
        Stopwatch sw
    )
    {
        sw.Stop();
        sink.Record(
            new ToolLogEntry(
                Turn: sink.CurrentTurn,
                Tool: tool,
                ArgsJson: argsJson,
                ResultJson: JsonSerializer.Serialize(result),
                DurationMs: sw.ElapsedMilliseconds
            )
        );
        return result;
    }

    private static object RecordFatal(
        ToolLogSink sink,
        string tool,
        string argsJson,
        string error,
        Stopwatch sw
    )
    {
        sw.Stop();
        var result = new
        {
            ok = false,
            error,
            fatal = true,
        };
        sink.Record(
            new ToolLogEntry(
                Turn: sink.CurrentTurn,
                Tool: tool,
                ArgsJson: argsJson,
                ResultJson: JsonSerializer.Serialize(result),
                DurationMs: sw.ElapsedMilliseconds,
                Error: error,
                Fatal: true
            )
        );
        return result;
    }

    private static object RecordError(
        ToolLogSink sink,
        string tool,
        string argsJson,
        Exception ex,
        Stopwatch sw
    )
    {
        sw.Stop();
        var result = new { ok = false, error = ex.Message };
        sink.Record(
            new ToolLogEntry(
                Turn: sink.CurrentTurn,
                Tool: tool,
                ArgsJson: argsJson,
                ResultJson: JsonSerializer.Serialize(result),
                DurationMs: sw.ElapsedMilliseconds,
                Error: ex.Message
            )
        );
        return result;
    }
}
