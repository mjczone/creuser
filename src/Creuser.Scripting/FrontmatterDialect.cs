namespace Creuser.Scripting;

/// <summary>
/// Per-language dialect for embedding a YAML frontmatter block in a source
/// file. The platform's frontmatter convention is uniform: a YAML payload
/// delimited by <c>---</c> markers, with the per-language wrapper handling
/// how the block sits in the file.
///
/// <para>
/// Five wrapper kinds in v0.1:
/// <list type="bullet">
///   <item><see cref="DialectKind.Markdown"/> — bare YAML between <c>---</c> at the very top, no comment prefix. Used for <c>.md</c>, <c>.mdx</c>, <c>.markdown</c>, <c>.astro</c>.</item>
///   <item><see cref="DialectKind.CStyle"/> — block-comment-wrapped YAML. Used for <c>.ts</c>, <c>.tsx</c>, <c>.js</c>, <c>.jsx</c>, <c>.cs</c>, <c>.go</c>, <c>.java</c>, <c>.rs</c>, <c>.cpp</c>, <c>.c</c>, <c>.scala</c>, <c>.kt</c>.</item>
///   <item><see cref="DialectKind.Hash"/> — line-comment-prefixed YAML. Used for <c>.py</c>, <c>.sh</c>, <c>.bash</c>, <c>.rb</c>, <c>.pl</c>, <c>.yaml</c>, <c>.yml</c>, <c>.toml</c>, <c>.ini</c>, <c>.r</c>, <c>.tf</c>.</item>
///   <item><see cref="DialectKind.Html"/> — HTML-comment-wrapped YAML. Used for <c>.html</c>, <c>.htm</c>, <c>.vue</c> (top-level template-only files; SFC handling deferred).</item>
///   <item><see cref="DialectKind.SqlDash"/> — <c>--</c>-prefixed line-comment YAML. Used for <c>.sql</c>.</item>
/// </list>
/// Files without a known dialect get a clear "unsupported file type" error.
/// </para>
///
/// <para>
/// The dialect also carries a <see cref="ShebangPrefix"/> that the parser
/// honors — for <c>.py</c> / <c>.sh</c> a leading <c>#!/usr/bin/env ...</c>
/// line is preserved when the frontmatter is inserted or replaced. Other
/// dialects don't care about shebangs.
/// </para>
/// </summary>
public enum DialectKind
{
    Markdown,
    CStyle,
    Hash,
    Html,
    SqlDash,
}

public sealed record FrontmatterDialect(
    DialectKind Kind,
    /// <summary>Token that opens the frontmatter block on its own line (or on the line of the YAML itself for <see cref="DialectKind.Markdown"/>).</summary>
    string Opener,
    /// <summary>Token that closes the block on its own line.</summary>
    string Closer,
    /// <summary>Per-line prefix prepended to every YAML line for line-comment dialects (<see cref="DialectKind.Hash"/>, <see cref="DialectKind.SqlDash"/>). Empty for block-comment / bare dialects.</summary>
    string LinePrefix,
    /// <summary>Comment prefix (without trailing space) the parser recognizes when stripping back to bare YAML. <see cref="LinePrefix"/> minus trailing whitespace.</summary>
    string LineStripPrefix,
    /// <summary>True if the dialect supports a <c>#!</c> shebang on the very first line that must be preserved across edits.</summary>
    bool SupportsShebang
);

public static class FrontmatterDialects
{
    // Bare YAML between `---`. Markdown / Astro convention.
    public static readonly FrontmatterDialect Markdown = new(
        Kind: DialectKind.Markdown,
        Opener: "---",
        Closer: "---",
        LinePrefix: "",
        LineStripPrefix: "",
        SupportsShebang: false
    );

    // Block-comment-wrapped YAML.
    //   /* ---
    //   key: value
    //   --- */
    public static readonly FrontmatterDialect CStyle = new(
        Kind: DialectKind.CStyle,
        Opener: "/* ---",
        Closer: "--- */",
        LinePrefix: "",
        LineStripPrefix: "",
        SupportsShebang: false
    );

    // Line-comment-prefixed YAML.
    //   # ---
    //   # key: value
    //   # ---
    public static readonly FrontmatterDialect Hash = new(
        Kind: DialectKind.Hash,
        Opener: "# ---",
        Closer: "# ---",
        LinePrefix: "# ",
        LineStripPrefix: "#",
        SupportsShebang: true
    );

    // HTML-comment-wrapped YAML.
    //   <!-- ---
    //   key: value
    //   --- -->
    public static readonly FrontmatterDialect Html = new(
        Kind: DialectKind.Html,
        Opener: "<!-- ---",
        Closer: "--- -->",
        LinePrefix: "",
        LineStripPrefix: "",
        SupportsShebang: false
    );

    // SQL line-comment-prefixed YAML.
    //   -- ---
    //   -- key: value
    //   -- ---
    public static readonly FrontmatterDialect SqlDash = new(
        Kind: DialectKind.SqlDash,
        Opener: "-- ---",
        Closer: "-- ---",
        LinePrefix: "-- ",
        LineStripPrefix: "--",
        SupportsShebang: false
    );

    /// <summary>
    /// Resolve a dialect from a file path. Returns null when the extension
    /// isn't recognized — the runner surfaces that as a typed error so the
    /// operator knows to either pick a different file or extend the
    /// dialect set.
    /// </summary>
    public static FrontmatterDialect? FromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".mdx" or ".markdown" or ".astro" => Markdown,
            ".ts"
            or ".tsx"
            or ".js"
            or ".jsx"
            or ".cjs"
            or ".mjs"
            or ".cs"
            or ".go"
            or ".java"
            or ".rs"
            or ".cpp"
            or ".c"
            or ".h"
            or ".hpp"
            or ".scala"
            or ".kt"
            or ".swift" => CStyle,
            ".py"
            or ".sh"
            or ".bash"
            or ".zsh"
            or ".rb"
            or ".pl"
            or ".yaml"
            or ".yml"
            or ".toml"
            or ".ini"
            or ".conf"
            or ".r"
            or ".tf"
            or ".dockerfile" => Hash,
            ".html" or ".htm" or ".vue" => Html,
            ".sql" => SqlDash,
            _ => null,
        };
    }
}
