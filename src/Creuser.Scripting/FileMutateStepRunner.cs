using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Creuser.Core.Execution;

namespace Creuser.Scripting;

/// <summary>
/// Declarative file-mutation runner. Inputs:
/// <code>
/// inputs:
///   ops:
///     - op: create
///       path: research/today/article.md
///       content: "..."
///     - op: modify
///       path: index.md
///       content: "new full content"
///     - op: delete
///       path: old/legacy.txt
///     - op: rename
///       path: src/foo.md
///       rename_to: docs/foo.md
/// </code>
///
/// <para>
/// Returns <see cref="FileChange"/> records — does <em>not</em> touch disk.
/// The executor's transactional commit path is what actually applies the
/// changes (see architecture.md "File mutation discipline"): all changes
/// from a successful step are staged + committed as one atomic git op.
/// A failure inside this runner — invalid op, missing source file,
/// path-escape — leaves no partial mutation because nothing was written.
/// </para>
///
/// <para>
/// <b>Op semantics:</b>
/// <list type="bullet">
///   <item><c>create</c> — file must not exist. Required: <c>content</c>.</item>
///   <item><c>modify</c> — file must exist. Required: <c>content</c>. Replaces with new content; before/after hashes recorded.</item>
///   <item><c>delete</c> — file must exist. Removes it; before-hash recorded.</item>
///   <item><c>rename</c> — file must exist at <c>path</c>; destination must not exist. Required: <c>rename_to</c>. Optional <c>content</c> updates the file at the new location.</item>
/// </list>
/// </para>
///
/// <para>
/// Patch-style ("apply this unified diff") is intentionally <em>not</em> in
/// v0.1 — the LLM-emit-then-mutate flow consistently produces full file
/// content rather than diffs, and the post-LLM safety check against the
/// returned-from-store source-of-record is cleaner with full replacement
/// than with patch application against potentially-drifted state. The
/// <c>code-edit</c> runner (post-v1) is where AST-aware surgical refactors
/// land — that's the right level for "change this function" semantics
/// rather than text-diff manipulation.
/// </para>
/// </summary>
public sealed class FileMutateStepRunner : IStepRunner
{
    public string StepType => "file-mutate";

    public Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(ctx.WorkingTreePath))
        {
            sw.Stop();
            return Task.FromResult(
                StepResult.Failure(
                    "file-mutate step requires a workspace working tree. The workspace may be unsupported (s3) or its clone may not exist yet — sync the workspace and retry.",
                    sw.ElapsedMilliseconds
                )
            );
        }
        if (!Directory.Exists(ctx.WorkingTreePath))
        {
            sw.Stop();
            return Task.FromResult(
                StepResult.Failure(
                    $"Working tree does not exist on disk: {ctx.WorkingTreePath}. For git workspaces, sync first; for local workspaces, verify the configured path.",
                    sw.ElapsedMilliseconds
                )
            );
        }

        if (!inputs.TryGetValue("ops", out var rawOps) || rawOps is null)
        {
            sw.Stop();
            return Task.FromResult(
                StepResult.Failure(
                    "file-mutate step requires an `ops` input — a list of file operations. Example: `inputs: { ops: [ { op: create, path: foo.md, content: \"...\" } ] }`.",
                    sw.ElapsedMilliseconds
                )
            );
        }

        if (rawOps is not IList<object?> opsList)
        {
            sw.Stop();
            return Task.FromResult(
                StepResult.Failure(
                    $"`ops` must be a list of objects; got {rawOps.GetType().Name}.",
                    sw.ElapsedMilliseconds
                )
            );
        }

        if (opsList.Count == 0)
        {
            sw.Stop();
            // Empty ops list isn't an error — a generated job might
            // legitimately produce no changes. Step succeeds with zero
            // FileChanges and the executor's apply-and-commit short-circuits
            // via NoCommit=true.
            return Task.FromResult(
                StepResult.Success(
                    new Dictionary<string, object?>
                    {
                        ["applied"] = 0,
                        ["paths"] = new List<object?>(),
                    },
                    sw.ElapsedMilliseconds
                )
            );
        }

        var changes = new List<FileChange>(opsList.Count);
        var paths = new List<object?>(opsList.Count);
        for (var i = 0; i < opsList.Count; i++)
        {
            var item = opsList[i];
            if (item is not IReadOnlyDictionary<string, object?> opDict)
            {
                sw.Stop();
                return Task.FromResult(
                    StepResult.Failure(
                        $"`ops[{i}]` must be an object with at least `op` and `path` keys.",
                        sw.ElapsedMilliseconds
                    )
                );
            }

            var (change, error) = TryBuildChange(ctx.WorkingTreePath, opDict, i);
            if (error is not null)
            {
                sw.Stop();
                return Task.FromResult(StepResult.Failure(error, sw.ElapsedMilliseconds));
            }
            changes.Add(change!);
            paths.Add(change!.Path);
        }

        sw.Stop();
        var outputs = new Dictionary<string, object?>
        {
            ["applied"] = changes.Count,
            ["paths"] = paths,
        };
        return Task.FromResult(
            new StepResult(
                Status: StepStatus.Succeeded,
                Outputs: outputs,
                FileChanges: changes,
                Artifacts: Array.Empty<StepArtifact>(),
                DurationMs: sw.ElapsedMilliseconds
            )
        );
    }

    private static (FileChange? Change, string? Error) TryBuildChange(
        string workingTree,
        IReadOnlyDictionary<string, object?> opDict,
        int index
    )
    {
        var op = GetString(opDict, "op")?.Trim().ToLowerInvariant();
        var path = GetString(opDict, "path")?.Trim();
        if (string.IsNullOrWhiteSpace(op))
            return (null, $"`ops[{index}].op` is required.");
        if (string.IsNullOrWhiteSpace(path))
            return (null, $"`ops[{index}].path` is required.");

        if (!TryResolveSafe(workingTree, path, out var fullPath, out var safeError))
            return (null, $"`ops[{index}]`: {safeError}");

        switch (op)
        {
            case "create":
            {
                if (File.Exists(fullPath))
                    return (
                        null,
                        $"`ops[{index}]` create: file already exists at '{path}'. Use `modify` to overwrite."
                    );
                var content = GetString(opDict, "content") ?? string.Empty;
                var bytes = Encoding.UTF8.GetBytes(content);
                return (
                    new FileChange(
                        Path: path,
                        Op: FileChangeOp.Create,
                        AfterHash: Sha256(bytes),
                        Content: bytes
                    ),
                    null
                );
            }
            case "modify":
            case "modify-replace":
            {
                if (!File.Exists(fullPath))
                    return (
                        null,
                        $"`ops[{index}]` modify: file does not exist at '{path}'. Use `create` to add."
                    );
                var content = GetString(opDict, "content");
                if (content is null)
                    return (null, $"`ops[{index}]` modify: `content` is required.");
                var beforeBytes = File.ReadAllBytes(fullPath);
                var newBytes = Encoding.UTF8.GetBytes(content);
                return (
                    new FileChange(
                        Path: path,
                        Op: FileChangeOp.Modify,
                        BeforeHash: Sha256(beforeBytes),
                        AfterHash: Sha256(newBytes),
                        Content: newBytes
                    ),
                    null
                );
            }
            case "delete":
            {
                if (!File.Exists(fullPath))
                    return (null, $"`ops[{index}]` delete: file does not exist at '{path}'.");
                var beforeBytes = File.ReadAllBytes(fullPath);
                return (
                    new FileChange(
                        Path: path,
                        Op: FileChangeOp.Delete,
                        BeforeHash: Sha256(beforeBytes)
                    ),
                    null
                );
            }
            case "rename":
            {
                if (!File.Exists(fullPath))
                    return (null, $"`ops[{index}]` rename: source '{path}' does not exist.");
                var renameTo = GetString(opDict, "rename_to")?.Trim();
                if (string.IsNullOrWhiteSpace(renameTo))
                    return (null, $"`ops[{index}]` rename: `rename_to` is required.");
                if (!TryResolveSafe(workingTree, renameTo, out var destFull, out var destError))
                    return (null, $"`ops[{index}]` rename: {destError}");
                if (File.Exists(destFull))
                    return (
                        null,
                        $"`ops[{index}]` rename: destination '{renameTo}' already exists."
                    );
                var beforeBytes = File.ReadAllBytes(fullPath);
                var contentOverride = GetString(opDict, "content");
                byte[]? content = contentOverride is null
                    ? null
                    : Encoding.UTF8.GetBytes(contentOverride);
                return (
                    new FileChange(
                        Path: path,
                        Op: FileChangeOp.Rename,
                        RenameTo: renameTo,
                        BeforeHash: Sha256(beforeBytes),
                        AfterHash: content is null ? Sha256(beforeBytes) : Sha256(content),
                        Content: content
                    ),
                    null
                );
            }
            default:
                return (
                    null,
                    $"`ops[{index}].op` = '{op}' is not a recognised operation. Expected one of: create, modify, delete, rename."
                );
        }
    }

    private static bool TryResolveSafe(
        string root,
        string relative,
        out string fullPath,
        out string error
    )
    {
        var trimmed = relative.TrimStart('/', '\\');
        var combined = Path.GetFullPath(Path.Combine(root, trimmed));
        var normalizedRoot = Path.GetFullPath(root);
        var rootWithSep = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (
            !combined.StartsWith(rootWithSep, StringComparison.Ordinal)
            && combined != normalizedRoot
        )
        {
            fullPath = string.Empty;
            error = $"path '{relative}' escapes the workspace root.";
            return false;
        }
        fullPath = combined;
        error = string.Empty;
        return true;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string? GetString(IReadOnlyDictionary<string, object?> dict, string key) =>
        dict.TryGetValue(key, out var v) ? v?.ToString() : null;
}
