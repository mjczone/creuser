using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Creuser.Scripting;

/// <summary>
/// Result of locating a frontmatter block in a file. <see cref="Existed"/>
/// distinguishes "no block in this file" from "empty block" — important
/// because the splicer needs to know whether to insert vs replace.
/// </summary>
public sealed record FoundFrontmatter(
    bool Existed,
    /// <summary>Raw YAML payload between the delimiters (already stripped of any line-comment prefix). Empty if <see cref="Existed"/> is false.</summary>
    string YamlPayload,
    /// <summary>Inclusive line index where the block opener sits in the file. -1 when the block doesn't exist.</summary>
    int OpenerLineIndex,
    /// <summary>Inclusive line index where the block closer sits in the file. -1 when the block doesn't exist.</summary>
    int CloserLineIndex,
    /// <summary>For dialects that support shebangs (<c>.sh</c>, <c>.py</c>): the line index of the shebang, or -1 if none.</summary>
    int ShebangLineIndex
);

/// <summary>
/// Reads + writes frontmatter blocks per <see cref="FrontmatterDialect"/>.
/// The reader is conservative — it identifies the block when the dialect's
/// opener sits at line 0 (or immediately after a shebang for shebang-aware
/// dialects). It does <em>not</em> scan the entire file looking for a block
/// in arbitrary positions; that's a footgun.
///
/// <para>
/// The writer always emits block-style YAML (one key per line, two-space
/// indent for nested values), wraps it in the dialect's opener/closer +
/// per-line prefix, and inserts at the right position. Existing
/// frontmatter is replaced byte-for-byte; the rest of the file is
/// preserved exactly.
/// </para>
/// </summary>
public static class FrontmatterIO
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .DisableAliases()
        .Build();

    /// <summary>
    /// Locate the frontmatter block in <paramref name="content"/> using the
    /// given dialect. Returns a <see cref="FoundFrontmatter"/> with
    /// <c>Existed=false</c> when no block is found at the file's start
    /// (or after the optional shebang).
    /// </summary>
    public static FoundFrontmatter Find(string content, FrontmatterDialect dialect)
    {
        var lines = SplitLines(content);
        var startScan = 0;
        var shebangIdx = -1;
        if (dialect.SupportsShebang && lines.Length > 0 && lines[0].StartsWith("#!"))
        {
            shebangIdx = 0;
            startScan = 1;
        }

        // Skip blank lines between the shebang and the block.
        while (startScan < lines.Length && string.IsNullOrWhiteSpace(lines[startScan]))
            startScan++;

        if (startScan >= lines.Length)
            return new FoundFrontmatter(
                Existed: false,
                YamlPayload: string.Empty,
                OpenerLineIndex: -1,
                CloserLineIndex: -1,
                ShebangLineIndex: shebangIdx
            );

        // The opener must match exactly on its own line.
        if (lines[startScan].TrimEnd() != dialect.Opener)
            return new FoundFrontmatter(
                Existed: false,
                YamlPayload: string.Empty,
                OpenerLineIndex: -1,
                CloserLineIndex: -1,
                ShebangLineIndex: shebangIdx
            );

        // Scan forward for the closer.
        for (var i = startScan + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == dialect.Closer)
            {
                // Lines between opener+1 and closer-1 are the YAML payload.
                var payload = ExtractPayload(lines, startScan + 1, i, dialect);
                return new FoundFrontmatter(
                    Existed: true,
                    YamlPayload: payload,
                    OpenerLineIndex: startScan,
                    CloserLineIndex: i,
                    ShebangLineIndex: shebangIdx
                );
            }
        }

        // Opener present but no closer — treat as no-block. The runner will
        // surface this as a parse error if the YAML doesn't deserialize.
        return new FoundFrontmatter(
            Existed: false,
            YamlPayload: string.Empty,
            OpenerLineIndex: -1,
            CloserLineIndex: -1,
            ShebangLineIndex: shebangIdx
        );
    }

    /// <summary>
    /// Parse the YAML payload into a normalized object map. Returns an
    /// empty dictionary for an empty payload. Throws
    /// <see cref="FrontmatterParseException"/> on malformed YAML.
    /// </summary>
    public static Dictionary<string, object?> ParsePayload(string yamlPayload)
    {
        if (string.IsNullOrWhiteSpace(yamlPayload))
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            var raw = Deserializer.Deserialize<object?>(yamlPayload);
            if (raw is null)
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            // Normalize to canonical shape so downstream code doesn't have
            // to handle YamlDotNet's object-keyed nested dicts.
            var normalized = InputsNormalizer.Normalize(raw);
            return normalized as Dictionary<string, object?>
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            throw new FrontmatterParseException(
                "YAML parse error in frontmatter: " + ex.Message,
                ex
            );
        }
    }

    /// <summary>
    /// Render the given values as YAML, then wrap in the dialect's
    /// opener/closer + per-line prefix. Returns just the block (no rest of
    /// the file).
    /// </summary>
    public static string SerializeBlock(
        IDictionary<string, object?> values,
        FrontmatterDialect dialect
    )
    {
        var sb = new StringBuilder();
        sb.Append(dialect.Opener).Append('\n');
        if (values.Count > 0)
        {
            var yaml = Serializer.Serialize(values).TrimEnd('\n');
            foreach (var line in yaml.Split('\n'))
            {
                sb.Append(dialect.LinePrefix).Append(line).Append('\n');
            }
        }
        sb.Append(dialect.Closer).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Splice a serialized frontmatter block into <paramref name="content"/>
    /// at the right position: replace existing block, or insert at the top
    /// (after a shebang for shebang-aware dialects). Returns the new file
    /// content.
    /// </summary>
    public static string Splice(
        string content,
        FrontmatterDialect dialect,
        string serializedBlock,
        FoundFrontmatter found
    )
    {
        var lines = SplitLines(content);

        if (found.Existed)
        {
            // Replace lines [OpenerLineIndex .. CloserLineIndex] with the new
            // block. Preserve everything before and after.
            var before = string.Join('\n', lines.Take(found.OpenerLineIndex));
            var after = string.Join('\n', lines.Skip(found.CloserLineIndex + 1));
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(before))
                sb.Append(before).Append('\n');
            sb.Append(serializedBlock);
            if (!string.IsNullOrEmpty(after))
                sb.Append(after);
            // Preserve a trailing newline if the original had one.
            if (content.EndsWith('\n') && sb.Length > 0 && sb[^1] != '\n')
                sb.Append('\n');
            return sb.ToString();
        }

        // No existing block. Insert after the shebang (or at the top).
        var insertIdx = found.ShebangLineIndex >= 0 ? found.ShebangLineIndex + 1 : 0;
        var sb2 = new StringBuilder();
        for (var i = 0; i < insertIdx; i++)
            sb2.Append(lines[i]).Append('\n');
        sb2.Append(serializedBlock);
        // A blank line between the new block and the rest of the file makes
        // the result more readable in code editors.
        if (insertIdx < lines.Length)
            sb2.Append('\n');
        for (var i = insertIdx; i < lines.Length; i++)
        {
            sb2.Append(lines[i]);
            if (i < lines.Length - 1)
                sb2.Append('\n');
        }
        if (content.EndsWith('\n') && sb2.Length > 0 && sb2[^1] != '\n')
            sb2.Append('\n');
        return sb2.ToString();
    }

    private static string ExtractPayload(
        string[] lines,
        int firstYamlLine,
        int closerIdx,
        FrontmatterDialect dialect
    )
    {
        var sb = new StringBuilder();
        for (var i = firstYamlLine; i < closerIdx; i++)
        {
            var line = lines[i];
            // Strip the per-line prefix for line-comment dialects. The
            // canonical pattern is `<prefix> <yaml>` but also tolerate
            // `<prefix><yaml>` (no space) for operator-edited files.
            if (!string.IsNullOrEmpty(dialect.LineStripPrefix))
            {
                if (line.StartsWith(dialect.LineStripPrefix + " ", StringComparison.Ordinal))
                    line = line[(dialect.LineStripPrefix.Length + 1)..];
                else if (line.StartsWith(dialect.LineStripPrefix, StringComparison.Ordinal))
                    line = line[dialect.LineStripPrefix.Length..];
                else if (string.IsNullOrWhiteSpace(line))
                    line = string.Empty;
            }
            sb.Append(line);
            if (i < closerIdx - 1)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string[] SplitLines(string content)
    {
        return content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
