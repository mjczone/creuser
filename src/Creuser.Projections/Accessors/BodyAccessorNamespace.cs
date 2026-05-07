using System.Text;
using System.Text.RegularExpressions;
using Creuser.Core.Projections;

namespace Creuser.Projections.Accessors;

/// <summary>
/// <c>body.*</c> accessors: things derived from the file's body (post-frontmatter).
/// Stage A scope: <c>title</c> (first H1) and <c>word_count</c>. Richer extractors
/// (link list, code-block list) land in Stage F.
/// </summary>
public sealed class BodyAccessorNamespace : IComputedAccessorNamespace
{
    public string Namespace => "body";
    public string? Description => "Facts derived from the file body, post-frontmatter.";

    private static readonly Regex H1 = new(
        @"^#\s+(?<title>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled
    );

    public IReadOnlyDictionary<string, AccessorField> Fields { get; } =
        new Dictionary<string, AccessorField>(StringComparer.Ordinal)
        {
            ["title"] = new(
                Name: "title",
                Description: "First Markdown H1 heading in the body, or null when none.",
                ReturnType: AccessorReturnType.String,
                Resolve: ctx =>
                {
                    var body = ReadBody(ctx);
                    if (body is null)
                        return null;
                    var match = H1.Match(body);
                    return match.Success ? match.Groups["title"].Value.Trim() : null;
                }
            ),
            ["word_count"] = new(
                Name: "word_count",
                Description: "Whitespace-separated token count of the body.",
                ReturnType: AccessorReturnType.Integer,
                Resolve: ctx =>
                {
                    var body = ReadBody(ctx);
                    if (string.IsNullOrWhiteSpace(body))
                        return 0;
                    return body.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    ).Length;
                }
            ),
        };

    /// <summary>
    /// Read the file body with the frontmatter stripped. Returns null when the
    /// file can't be read; returns the entire content when no frontmatter
    /// fence is present.
    /// </summary>
    private static string? ReadBody(AccessorContext ctx)
    {
        byte[] bytes;
        try
        {
            bytes = ctx.ReadBytes is not null ? ctx.ReadBytes() : File.ReadAllBytes(ctx.FullPath);
        }
        catch
        {
            return null;
        }
        var text = Encoding.UTF8.GetString(bytes);
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var after = end + 4;
                if (after < text.Length && text[after] == '\n')
                    after++;
                return text[after..];
            }
        }
        return text;
    }
}
