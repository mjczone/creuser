using Creuser.Core.Projections;

namespace Creuser.Projections.Accessors;

/// <summary>
/// <c>file.*</c> accessors: things you can answer from the file's bytes and
/// filesystem metadata alone.
/// </summary>
public sealed class FileAccessorNamespace : IComputedAccessorNamespace
{
    public string Namespace => "file";
    public string? Description => "Filesystem-level facts about the matched file.";

    public IReadOnlyDictionary<string, AccessorField> Fields { get; } =
        new Dictionary<string, AccessorField>(StringComparer.Ordinal)
        {
            ["line_count"] = new(
                Name: "line_count",
                Description: "Newline count of the file body (UTF-8 read).",
                ReturnType: AccessorReturnType.Integer,
                Resolve: ctx => CountLines(ctx.FullPath)
            ),
            ["size"] = new(
                Name: "size",
                Description: "Byte length of the file on disk.",
                ReturnType: AccessorReturnType.Integer,
                Resolve: ctx => new FileInfo(ctx.FullPath).Length
            ),
            ["mtime"] = new(
                Name: "mtime",
                Description: "ISO-8601 UTC last-write timestamp.",
                ReturnType: AccessorReturnType.DateTime,
                Resolve: ctx => File.GetLastWriteTimeUtc(ctx.FullPath).ToString("O")
            ),
            ["extension"] = new(
                Name: "extension",
                Description: "File extension including the leading dot (lowercased).",
                ReturnType: AccessorReturnType.String,
                Resolve: ctx => Path.GetExtension(ctx.FullPath).ToLowerInvariant()
            ),
        };

    private static int CountLines(string fullPath)
    {
        var n = 0;
        using var reader = new StreamReader(fullPath);
        while (reader.ReadLine() is not null)
            n++;
        return n;
    }
}
