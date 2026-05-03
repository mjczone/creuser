namespace Creuser.Scripting.ToolLoop;

/// <summary>
/// Resolves a caller-supplied relative path against a trusted root and
/// rejects any path that escapes that root via <c>..</c> segments, absolute
/// paths, or platform-specific quirks.
///
/// <para>
/// The same logic was first written for <c>FileMutateStepRunner</c>; lifted
/// here so the tool-loop registry's read-only file tools share the
/// invariant. Any future runner that takes operator- or LLM-supplied paths
/// against the working tree should route through this helper.
/// </para>
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Try to resolve <paramref name="relative"/> under <paramref name="root"/>.
    /// On success, <paramref name="fullPath"/> holds the absolute path under
    /// the root. On failure, <paramref name="error"/> describes why and the
    /// caller should treat the request as fatal — never retry.
    /// </summary>
    public static bool TryResolveSafe(
        string root,
        string relative,
        out string fullPath,
        out string error
    )
    {
        // Empty relative path resolves to the root itself.
        var trimmed = (relative ?? string.Empty).TrimStart('/', '\\');
        var combined = string.IsNullOrEmpty(trimmed)
            ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(root, trimmed));
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
}
