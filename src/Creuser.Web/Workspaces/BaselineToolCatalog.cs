using Creuser.Core.Execution;

namespace Creuser.Web.Workspaces;

/// <summary>
/// Static <see cref="IToolCatalog"/> implementation that mirrors the
/// curated tool palette baked into the <c>:latest</c> (fat) Docker image —
/// see <c>docs/docker-variants.md</c>. The Jobs picker uses these as the
/// suggested entries; admins can still type custom commands they've added
/// via a derivative image.
///
/// <para>
/// Categories match the architecture doc's "Container tooling" section:
/// <c>core</c>, <c>code-aware</c>, <c>schema-data</c>, <c>diff-merge</c>,
/// <c>runtime</c>, <c>specialized</c>. Plus a <c>system</c> category for
/// POSIX utilities every reasonable Linux base ships with.
/// </para>
/// </summary>
public sealed class BaselineToolCatalog : IToolCatalog
{
    private static readonly IReadOnlyList<ToolEntry> Entries =
    [
        // POSIX / base — always present on Linux. Listed so operators can
        // pick them rather than typing each one by hand.
        new("cat", "system", "Concatenate / display files.", "system"),
        new("cp", "system", "Copy files.", "system"),
        new("mv", "system", "Move / rename files.", "system"),
        new("mkdir", "system", "Create directories.", "system"),
        new("ls", "system", "List directory entries.", "system"),
        new("find", "system", "Recursive directory search.", "system"),
        new("grep", "system", "Pattern match in files.", "system"),
        new("sed", "system", "Stream editor.", "system"),
        new("awk", "system", "Pattern scanning + processing.", "system"),
        new("xargs", "system", "Build commands from stdin args.", "system"),
        new("sort", "system", "Sort lines.", "system"),
        new("uniq", "system", "Filter consecutive duplicate lines.", "system"),
        new("head", "system", "Output first N lines.", "system"),
        new("tail", "system", "Output last N lines.", "system"),
        new("wc", "system", "Word / line / byte count.", "system"),
        new("tr", "system", "Translate / squeeze characters.", "system"),
        new("cut", "system", "Cut sections from lines.", "system"),
        // Core text & search (baseline image).
        new("git", "core", "Version control.", "baseline"),
        new("rg", "core", "ripgrep — fast recursive grep.", "baseline"),
        new("fd", "core", "fd — fast `find` alternative.", "baseline"),
        new("jq", "core", "JSON processor.", "baseline"),
        new("yq", "core", "YAML processor (jq-like).", "baseline"),
        new("xq", "core", "XML processor (jq-like).", "baseline"),
        new("tree", "core", "Directory tree printer.", "baseline"),
        new("bat", "core", "`cat` with syntax highlighting.", "baseline"),
        // Code-aware.
        new("ast-grep", "code-aware", "Structural code search + rewrite.", "baseline"),
        new("tree-sitter", "code-aware", "Parser CLI for many languages.", "baseline"),
        new("srgn", "code-aware", "Surgical refactor tool.", "baseline"),
        // Schema & data.
        new("psql", "schema-data", "PostgreSQL CLI.", "baseline"),
        new("redis-cli", "schema-data", "Redis CLI.", "baseline"),
        new("sqlite3", "schema-data", "SQLite CLI.", "baseline"),
        new("csvcut", "schema-data", "csvkit — extract CSV columns.", "baseline"),
        new("csvjoin", "schema-data", "csvkit — join CSVs.", "baseline"),
        new("csvstat", "schema-data", "csvkit — column stats.", "baseline"),
        new("csvgrep", "schema-data", "csvkit — search CSV rows.", "baseline"),
        // Diff & merge.
        new("delta", "diff-merge", "Git diff / log syntax-highlighter.", "baseline"),
        new("difft", "diff-merge", "Tree-aware structural diff.", "baseline"),
        new("diff-so-fancy", "diff-merge", "Improved diff output.", "baseline"),
        // Language runtimes.
        new("node", "runtime", "Node.js 24 LTS.", "baseline"),
        new("npm", "runtime", "npm package manager.", "baseline"),
        new("npx", "runtime", "Run npm-installed binaries.", "baseline"),
        new("python", "runtime", "Python 3.13.", "baseline"),
        new("python3", "runtime", "Python 3.13 (versioned name).", "baseline"),
        new("uv", "runtime", "Astral uv — Python package manager + runner.", "baseline"),
        new("dotnet", "runtime", ".NET 10 SDK / runtime.", "baseline"),
        // Specialized.
        new("atlas", "specialized", "Schema-as-code DDL planner.", "baseline"),
        new("dbmate", "specialized", "Database migrations.", "baseline"),
        new("migra", "specialized", "Postgres schema diff.", "baseline"),
    ];

    public IReadOnlyList<ToolEntry> List() => Entries;
}
