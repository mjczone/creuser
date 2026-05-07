namespace Creuser.Projections.Conventions;

/// <summary>
/// Bundled <c>creuser:standard/*</c> convention library. Workspaces can
/// reference these via <c>extends:</c> in their per-workspace YAML, or
/// drop them in unmodified by writing a one-line file:
/// <code>
/// id: business_rule
/// extends: creuser:standard/business-rule
/// </code>
///
/// <para>
/// Each entry is a raw YAML string (rather than a parsed <c>Convention</c>)
/// so the merge logic can reuse the same loader. A static catalog keeps the
/// allocation cost tiny — no I/O on lookup, no caching layer to invalidate.
/// </para>
/// </summary>
public static class StandardConventions
{
    public static IReadOnlyDictionary<string, string> Library { get; } = BuildLibrary();

    public static bool TryGet(string reference, out string yaml)
    {
        if (Library.TryGetValue(reference, out var v))
        {
            yaml = v;
            return true;
        }
        yaml = string.Empty;
        return false;
    }

    private static IReadOnlyDictionary<string, string> BuildLibrary()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        d["creuser:standard/markdown-doc"] = """
            id: markdown_doc
            description: Generic markdown documentation. Low priority — workspace conventions override.
            priority: 0
            match:
              glob: "**/*.md"
              exclude:
                - "**/node_modules/**"
                - "**/.git/**"
                - "**/_drafts/**"
            slug:
              from: path
              transform: kebab
            metadata:
              source: frontmatter
            """;
        d["creuser:standard/adr"] = """
            id: adr
            description: Architecture Decision Records.
            priority: 50
            match:
              glob: "docs/adr/*.md"
              exclude: []
            slug:
              from: filename
              transform: as-is
            metadata:
              source: frontmatter
            """;
        d["creuser:standard/rfc"] = """
            id: rfc
            description: Request For Comments documents.
            priority: 50
            match:
              glob: "docs/rfc/*.md"
              exclude: []
            slug:
              from: filename
              transform: as-is
            metadata:
              source: frontmatter
            """;
        d["creuser:standard/skill"] = """
            id: skill
            description: Claude Code-style skill directories.
            priority: 60
            match:
              glob: "**/SKILL.md"
              exclude:
                - "**/node_modules/**"
                - "**/.git/**"
            slug:
              from: path
              transform: kebab
            metadata:
              source: frontmatter
            """;
        d["creuser:standard/migration-sql"] = """
            id: migration_sql
            description: SQL migration files.
            priority: 50
            match:
              glob: "**/migrations/*.sql"
              exclude:
                - "**/node_modules/**"
            slug:
              from: filename
              transform: as-is
            metadata:
              source: none
            """;
        d["creuser:standard/business-rule"] = """
            id: business_rule
            description: Markdown files declaring business rules.
            priority: 50
            match:
              glob: "business-rules/**/*.md"
              exclude:
                - "business-rules/_drafts/**"
                - "business-rules/**/index.md"
            slug:
              from: filename
              transform: kebab
            metadata:
              source: frontmatter
            relationships:
              - kind: parent
                name: Parent
                icon: folder
                description: The directory's index.md, treated as the parent rule.
                order: 10
                select_path: "{file_dir}/index.md"
                target_kind: business_rule
                inverse: children
                inverse_name: Children
                inverse_icon: folder
              - kind: references
                name: References
                icon: link
                description: Other business rules this rule cites.
                order: 20
                select_frontmatter: references
                target_kind: business_rule
                inverse: referenced_by
                inverse_name: Referenced by
            """;
        return d;
    }
}
