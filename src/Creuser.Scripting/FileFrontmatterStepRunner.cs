using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Creuser.Core.Execution;

namespace Creuser.Scripting;

/// <summary>
/// Declarative frontmatter manipulation across many file types. The runner
/// is the architectural seam for the platform's <em>indexable metadata</em>
/// vision (see architecture.md "Frontmatter as cross-file metadata"):
/// add / update / remove keys in a YAML block embedded in source files of
/// any supported language, then a follow-on step (often <c>llm-chat</c>
/// or a custom <c>node</c>/<c>python</c> indexer) reads those keys to
/// build cross-references, search indexes, and category groupings.
///
/// <para>
/// Inputs:
/// <code>
/// type: file-frontmatter
/// inputs:
///   ops:
///     - path: docs/intro.md
///       set:
///         title: "Introduction"
///         category: "core"
///         tags: ["api", "draft"]
///     - path: src/foo.ts
///       unset:
///         - todo
///         - draft
///     - path: src/bar.cs
///       replace:
///         category: "domain"
///         owner: "team-a"
/// </code>
/// </para>
///
/// <para>
/// Op semantics — one op per file (multiple ops can target the same file
/// in sequence):
/// <list type="bullet">
///   <item><c>set</c> — merge the given keys into the existing block (creating it if absent). Overwrites at the top level of the dict; nested objects are replaced wholesale (no deep merge in v0.1).</item>
///   <item><c>unset</c> — list of keys to remove. No-op if a key is absent.</item>
///   <item><c>replace</c> — replace the whole frontmatter with the given map. Effectively `clear + set`.</item>
/// </list>
/// </para>
///
/// <para>
/// File type is auto-detected from the path's extension via
/// <see cref="FrontmatterDialects.FromPath"/>. Files with unsupported
/// extensions return a typed error so the operator knows to extend the
/// dialect set rather than silently writing a malformed block.
/// </para>
///
/// <para>
/// Like <c>file-mutate</c>, the runner returns <see cref="FileChange"/>
/// records and never touches disk directly — the executor's
/// <see cref="IWorkspaceWorkingTree.ApplyAndCommitAsync"/> does the
/// transactional apply + commit per step.
/// </para>
/// </summary>
public sealed class FileFrontmatterStepRunner : IStepRunner
{
    public string StepType => "file-frontmatter";

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(ctx.WorkingTreePath) || !Directory.Exists(ctx.WorkingTreePath))
        {
            sw.Stop();
            return StepResult.Failure(
                "file-frontmatter step requires a workspace working tree. Sync the workspace first, or verify the local path is configured.",
                sw.ElapsedMilliseconds
            );
        }

        if (!inputs.TryGetValue("ops", out var rawOps) || rawOps is not IList<object?> opsList)
        {
            sw.Stop();
            return StepResult.Failure(
                "file-frontmatter step requires an `ops` input — a list of `{ path, set?|unset?|replace? }` operations.",
                sw.ElapsedMilliseconds
            );
        }

        if (opsList.Count == 0)
        {
            sw.Stop();
            return StepResult.Success(
                new Dictionary<string, object?>
                {
                    ["applied"] = 0,
                    ["paths"] = new List<object?>(),
                },
                sw.ElapsedMilliseconds
            );
        }

        var changes = new List<FileChange>(opsList.Count);
        var paths = new List<object?>(opsList.Count);
        for (var i = 0; i < opsList.Count; i++)
        {
            if (opsList[i] is not IReadOnlyDictionary<string, object?> opDict)
            {
                sw.Stop();
                return StepResult.Failure(
                    $"`ops[{i}]` must be an object with at least `path` and one of `set` / `unset` / `replace`.",
                    sw.ElapsedMilliseconds
                );
            }

            var (change, error) = await TryProcessAsync(ctx.WorkingTreePath, opDict, i, ct);
            if (error is not null)
            {
                sw.Stop();
                return StepResult.Failure(error, sw.ElapsedMilliseconds);
            }
            if (change is not null)
            {
                changes.Add(change);
                paths.Add(change.Path);
            }
        }

        sw.Stop();
        var outputs = new Dictionary<string, object?>
        {
            ["applied"] = changes.Count,
            ["paths"] = paths,
        };
        return new StepResult(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: changes,
            Artifacts: Array.Empty<StepArtifact>(),
            DurationMs: sw.ElapsedMilliseconds
        );
    }

    private static async Task<(FileChange? Change, string? Error)> TryProcessAsync(
        string workingTree,
        IReadOnlyDictionary<string, object?> opDict,
        int index,
        CancellationToken ct
    )
    {
        var path = GetString(opDict, "path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return (null, $"`ops[{index}].path` is required.");

        if (!TryResolveSafe(workingTree, path, out var fullPath, out var safeError))
            return (null, $"`ops[{index}]`: {safeError}");

        if (!File.Exists(fullPath))
            return (null, $"`ops[{index}]`: file does not exist at '{path}'.");

        var dialect = FrontmatterDialects.FromPath(path);
        if (dialect is null)
            return (
                null,
                $"`ops[{index}]`: file extension on '{path}' is not a supported frontmatter dialect. See architecture.md \"Frontmatter dialects\" for the supported list."
            );

        // Determine which op the dict is asking for. Exactly one of set /
        // unset / replace must be present.
        var hasSet = opDict.ContainsKey("set");
        var hasUnset = opDict.ContainsKey("unset");
        var hasReplace = opDict.ContainsKey("replace");
        var opCount = (hasSet ? 1 : 0) + (hasUnset ? 1 : 0) + (hasReplace ? 1 : 0);
        if (opCount == 0)
            return (
                null,
                $"`ops[{index}]`: must specify exactly one of `set`, `unset`, `replace`."
            );
        if (opCount > 1)
            return (null, $"`ops[{index}]`: specify only one of `set`, `unset`, `replace` per op.");

        var content = await File.ReadAllTextAsync(fullPath, ct);
        var found = FrontmatterIO.Find(content, dialect);

        Dictionary<string, object?> values;
        try
        {
            values = found.Existed
                ? FrontmatterIO.ParsePayload(found.YamlPayload)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (FrontmatterParseException ex)
        {
            return (null, $"`ops[{index}]`: {ex.Message}");
        }

        if (hasReplace)
        {
            if (opDict["replace"] is not IReadOnlyDictionary<string, object?> replaceMap)
                return (null, $"`ops[{index}].replace` must be an object.");
            values = new Dictionary<string, object?>(replaceMap, StringComparer.Ordinal);
        }
        else if (hasSet)
        {
            if (opDict["set"] is not IReadOnlyDictionary<string, object?> setMap)
                return (null, $"`ops[{index}].set` must be an object.");
            foreach (var kv in setMap)
                values[kv.Key] = kv.Value;
        }
        else if (hasUnset)
        {
            if (opDict["unset"] is not IList<object?> unsetList)
                return (null, $"`ops[{index}].unset` must be a list of key names.");
            foreach (var entry in unsetList)
            {
                var key = entry?.ToString();
                if (!string.IsNullOrEmpty(key))
                    values.Remove(key);
            }
        }

        var serialized = FrontmatterIO.SerializeBlock(values, dialect);
        var newContent = FrontmatterIO.Splice(content, dialect, serialized, found);

        var beforeBytes = Encoding.UTF8.GetBytes(content);
        var afterBytes = Encoding.UTF8.GetBytes(newContent);
        if (StructurallyEqual(beforeBytes, afterBytes))
        {
            // No-op — the requested change leaves the file byte-identical.
            // Returning null skips this op without aborting the step.
            return (null, null);
        }

        return (
            new FileChange(
                Path: path,
                Op: FileChangeOp.Modify,
                BeforeHash: Sha256(beforeBytes),
                AfterHash: Sha256(afterBytes),
                Content: afterBytes
            ),
            null
        );
    }

    private static bool StructurallyEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
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
