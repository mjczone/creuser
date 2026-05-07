using Creuser.Core.Projections;

namespace Creuser.Projections.Accessors;

/// <summary>
/// <c>path.*</c> accessors: facts about the entity's path within the working
/// tree. Computed against <see cref="AccessorContext.RelativePath"/> so values
/// are stable across machines.
/// </summary>
public sealed class PathAccessorNamespace : IComputedAccessorNamespace
{
    public string Namespace => "path";
    public string? Description => "Working-tree-relative path components.";

    public IReadOnlyDictionary<string, AccessorField> Fields { get; } =
        new Dictionary<string, AccessorField>(StringComparer.Ordinal)
        {
            ["filename"] = new(
                Name: "filename",
                Description: "Filename including extension.",
                ReturnType: AccessorReturnType.String,
                Resolve: ctx => Path.GetFileName(ctx.RelativePath)
            ),
            ["stem"] = new(
                Name: "stem",
                Description: "Filename without extension.",
                ReturnType: AccessorReturnType.String,
                Resolve: ctx => Path.GetFileNameWithoutExtension(ctx.RelativePath)
            ),
            ["extension"] = new(
                Name: "extension",
                Description: "Path's file extension (with leading dot).",
                ReturnType: AccessorReturnType.String,
                Resolve: ctx => Path.GetExtension(ctx.RelativePath)
            ),
            ["file_dir"] = new(
                Name: "file_dir",
                Description: "Working-tree-relative directory containing the file.",
                ReturnType: AccessorReturnType.String,
                Resolve: ctx =>
                    Path.GetDirectoryName(ctx.RelativePath)?.Replace('\\', '/') ?? string.Empty
            ),
            ["parent_dir"] = new(
                Name: "parent_dir",
                Description: "Last segment of the containing directory.",
                ReturnType: AccessorReturnType.String,
                Resolve: ctx =>
                {
                    var dir = Path.GetDirectoryName(ctx.RelativePath)?.Replace('\\', '/');
                    return string.IsNullOrEmpty(dir) ? string.Empty : Path.GetFileName(dir);
                }
            ),
            ["depth"] = new(
                Name: "depth",
                Description: "Number of directory segments above the file (root = 0).",
                ReturnType: AccessorReturnType.Integer,
                Resolve: ctx =>
                {
                    var rel = ctx.RelativePath.Replace('\\', '/').Trim('/');
                    if (string.IsNullOrEmpty(rel))
                        return 0;
                    return rel.Count(c => c == '/');
                }
            ),
            ["segments"] = new(
                Name: "segments",
                Description: "Path split on '/' as an ordered list of segments.",
                ReturnType: AccessorReturnType.StringArray,
                Resolve: ctx =>
                    ctx.RelativePath.Replace('\\', '/')
                        .Split('/', StringSplitOptions.RemoveEmptyEntries)
            ),
        };
}
