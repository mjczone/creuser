using System.Text.Json;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Projections.Conventions;
using Creuser.Projections.Scanner;

namespace Creuser.Projections.Tests;

public class ProjectionScannerTests : IAsyncLifetime
{
    private string _root = null!;
    private Workspace _workspace = null!;
    private ProjectionScanner _scanner = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"creuser-scanner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _workspace = new Workspace(
            Id: Guid.NewGuid(),
            Slug: "ws",
            Name: "Test",
            Description: null,
            Type: "local",
            Settings: "{}",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            CreatedBy: null
        );
        _scanner = new ProjectionScanner(TimeProvider.System);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Scan_OneConventionMatchesFiles_ProducesEntities()
    {
        await SeedFile("business-rules/auth/login.md", "---\ntitle: Login\nowner: alice\n---\n");
        await SeedFile("business-rules/auth/logout.md", "---\ntitle: Logout\nowner: bob\n---\n");
        await SeedFile("README.md", "ignored");

        var convention = ParseConvention(
            """
            id: business_rule
            match:
              glob: "business-rules/**/*.md"
            slug:
              from: filename
              transform: kebab
            metadata:
              source: frontmatter
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });

        Assert.Equal(2, result.Entities.Count);
        Assert.Contains(result.Entities, e => e.Slug == "login");
        Assert.Contains(result.Entities, e => e.Slug == "logout");
        // README.md is not under business-rules/ — not matched.
        Assert.DoesNotContain(result.Entities, e => e.Path == "README.md");
    }

    [Fact]
    public async Task Scan_FrontmatterIsParsedIntoMetadata()
    {
        await SeedFile(
            "business-rules/auth/login.md",
            "---\ntitle: Login\nowner: alice\nversion: 1.2\n---\nbody"
        );

        var convention = ParseConvention(
            """
            id: business_rule
            match:
              glob: "business-rules/**/*.md"
            slug:
              from: filename
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });
        Assert.Single(result.Entities);
        var entity = result.Entities[0];
        var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            entity.MetadataJson
        )!;
        Assert.Equal("Login", meta["title"].GetString());
        Assert.Equal("alice", meta["owner"].GetString());
    }

    [Fact]
    public async Task Scan_TwoConventionsConflict_HigherPriorityWins()
    {
        await SeedFile("business-rules/auth/login.md", "---\ntitle: Login\n---\n");

        var general = ParseConvention(
            """
            id: markdown_doc
            priority: 0
            match:
              glob: "**/*.md"
            slug:
              from: filename
            """
        );
        var specific = ParseConvention(
            """
            id: business_rule
            priority: 100
            match:
              glob: "business-rules/**/*.md"
            slug:
              from: filename
            """
        );

        var result = _scanner.Scan(_workspace, _root, new[] { general, specific });
        Assert.Single(result.Entities);
        Assert.Equal("business_rule", result.Entities[0].Kind);
    }

    [Fact]
    public async Task Scan_RelationshipResolvedByPath_ProducesEdge()
    {
        await SeedFile("business-rules/auth/index.md", "---\ntitle: Auth\n---\n");
        await SeedFile("business-rules/auth/login.md", "---\ntitle: Login\n---\n");

        // Two conventions: an index kind that picks up index.md, and a rule
        // kind that picks up the leaf files and references parent via path.
        var indexConv = ParseConvention(
            """
            id: business_rule_index
            priority: 100
            match:
              glob: "business-rules/**/index.md"
            slug:
              from: path
              transform: kebab
            """
        );
        var ruleConv = ParseConvention(
            """
            id: business_rule
            priority: 50
            match:
              glob: "business-rules/**/*.md"
              exclude:
                - "business-rules/**/index.md"
            slug:
              from: filename
            relationships:
              - kind: parent
                select_path: "{file_dir}/index.md"
                target_kind: business_rule_index
            """
        );

        var result = _scanner.Scan(_workspace, _root, new[] { indexConv, ruleConv });
        Assert.Equal(2, result.Entities.Count);
        Assert.Single(result.Refs);
        var edge = result.Refs[0];
        Assert.Equal("parent", edge.Relationship);
        Assert.NotNull(edge.ToEntityId);
        Assert.Equal(1, result.Report.RefsResolved);
    }

    [Fact]
    public async Task Scan_FrontmatterRefMissing_PersistsAsUnresolved()
    {
        await SeedFile(
            "business-rules/login.md",
            "---\ntitle: Login\nimplements:\n  - undefined-spec\n---\n"
        );

        var convention = ParseConvention(
            """
            id: business_rule
            match:
              glob: "business-rules/**/*.md"
            slug:
              from: filename
            relationships:
              - kind: implements
                select_frontmatter: implements
                target_kind: business_rule
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });
        Assert.Single(result.Entities);
        Assert.Single(result.Refs);
        Assert.Null(result.Refs[0].ToEntityId);
        Assert.Equal("undefined-spec", result.Refs[0].TargetSlug);
        Assert.Equal(1, result.Report.RefsUnresolved);
    }

    [Fact]
    public async Task Scan_RequiredFieldMissing_FlaggedAsSchemaFailure()
    {
        await SeedFile("docs/foo.md", "---\ntitle: Foo\n---\n");

        var convention = ParseConvention(
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            metadata:
              source: frontmatter
              required:
                - owner
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });
        Assert.Single(result.Entities);
        Assert.Equal(1, result.Report.SchemaFailures);
    }

    [Fact]
    public async Task Scan_ComputedAccessor_FilePathBody_AllResolve()
    {
        await SeedFile(
            "docs/foo/bar.md",
            "---\ntitle: Manual\n---\n# Heading\n\nThe quick brown fox jumps.\n"
        );

        var convention = ParseConvention(
            """
            id: doc
            match:
              glob: "docs/**/*.md"
            slug:
              from: filename
            metadata:
              source: frontmatter
              computed:
                lines: file.line_count
                size: file.size
                stem: path.stem
                parent: path.parent_dir
                heading: body.title
                words: body.word_count
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });
        Assert.Single(result.Entities);
        var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            result.Entities[0].MetadataJson
        )!;
        Assert.True(meta["lines"].GetInt32() >= 3);
        Assert.True(meta["size"].GetInt64() > 0);
        Assert.Equal("bar", meta["stem"].GetString());
        Assert.Equal("foo", meta["parent"].GetString());
        Assert.Equal("Heading", meta["heading"].GetString());
        // Body is `# Heading\n\nThe quick brown fox jumps.\n` — 7 whitespace tokens incl. `#` and `Heading`.
        Assert.Equal(7, meta["words"].GetInt32());
    }

    [Fact]
    public async Task Scan_UnknownComputedAccessor_QuietlySkipped()
    {
        await SeedFile("docs/foo.md", "---\ntitle: Foo\n---\nbody");
        var convention = ParseConvention(
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            metadata:
              source: frontmatter
              computed:
                bogus: bogus.namespace_field
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });
        Assert.Single(result.Entities);
        var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            result.Entities[0].MetadataJson
        )!;
        // Unknown accessor: not in metadata; scan succeeds without throwing.
        Assert.False(meta.ContainsKey("bogus"));
    }

    [Fact]
    public async Task Scan_FrontmatterMixedList_AutoClassifiesPathGlobUrl()
    {
        await SeedFile("docs/ADR/0001-foo.md", "---\ntitle: Foo\n---\n");
        await SeedFile("docs/PLANS/strategy.md", "---\ntitle: Strategy\n---\n");
        await SeedFile("packages/database/db.ts", "export const x = 1;");
        await SeedFile("packages/database/repo.ts", "export const y = 2;");
        await SeedFile(
            "docs/ADR/0014-foo.md",
            "---\ntitle: Foo\nrelated:\n  - docs/ADR/0001-foo.md\n  - docs/PLANS/strategy.md\n  - packages/database/**/*.ts\n  - https://example.com/issue/42\n---\n"
        );

        var adrConvention = ParseConvention(
            """
            id: adr
            priority: 100
            match:
              glob: "docs/ADR/**/*.md"
            slug:
              from: filename
            relationships:
              - kind: related
                name: Related
                source: frontmatter.related
                interpret: auto
                target_kind: any
            """
        );
        var planConvention = ParseConvention(
            """
            id: plan
            priority: 90
            match:
              glob: "docs/PLANS/**/*.md"
            slug:
              from: filename
            """
        );

        var result = _scanner.Scan(_workspace, _root, new[] { adrConvention, planConvention });

        // 2 ADRs + 1 plan = 3 entities (database/.ts files don't match a convention).
        Assert.Equal(3, result.Entities.Count);

        var refsFrom14 = result
            .Refs.Where(r =>
                r.FromEntityId
                == result.Entities.First(e => e.Kind == "adr" && e.Slug == "0014-foo").Id
            )
            .ToList();

        // Should produce: 1 path→ADR + 1 path→plan + 2 glob-expanded files + 1 URL = 5 refs
        Assert.Equal(5, refsFrom14.Count);

        var resolvedToEntity = refsFrom14.Where(r => r.ToEntityId is not null).ToList();
        Assert.Equal(2, resolvedToEntity.Count); // ADR 0001 and the plan resolve as entities.

        var unresolved = refsFrom14.Where(r => r.ToEntityId is null).ToList();
        Assert.Equal(3, unresolved.Count); // 2 file refs (.ts not matched), 1 url

        // Check structured metadata on a glob-expanded ref
        var globRef = refsFrom14.First(r =>
            r.MetadataJson is not null && r.MetadataJson.Contains("expanded_from")
        );
        var globMeta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            globRef.MetadataJson!
        )!;
        Assert.Equal("file", globMeta["kind"].GetString());
        Assert.Equal("packages/database/**/*.ts", globMeta["expanded_from"].GetString());

        // URL ref carries metadata.kind=url
        var urlRef = refsFrom14.First(r =>
            r.MetadataJson is not null && r.MetadataJson.Contains("\"url\"")
        );
        var urlMeta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            urlRef.MetadataJson!
        )!;
        Assert.Equal("url", urlMeta["kind"].GetString());
        Assert.Equal("https://example.com/issue/42", urlMeta["url"].GetString());
    }

    [Fact]
    public async Task Scan_FilterGlob_CarvesOneListIntoTypedFolders()
    {
        await SeedFile("docs/ADR/0001-foo.md", "---\ntitle: Foo\n---\n");
        await SeedFile("docs/PLANS/strategy.md", "---\ntitle: Strategy\n---\n");
        await SeedFile(
            "docs/ADR/0014-foo.md",
            "---\ntitle: Foo\nrelated:\n  - docs/ADR/0001-foo.md\n  - docs/PLANS/strategy.md\n---\n"
        );

        var adr = ParseConvention(
            """
            id: adr
            priority: 100
            match:
              glob: "docs/ADR/**/*.md"
            slug:
              from: filename
            relationships:
              - kind: related_adrs
                name: Related ADRs
                source: frontmatter.related
                filter:
                  kind: glob
                  pattern: "docs/ADR/**/*.md"
                interpret: path
                target_kind: adr
              - kind: related_plans
                name: Related Plans
                source: frontmatter.related
                filter:
                  kind: glob
                  pattern: "docs/PLANS/**/*.md"
                interpret: path
                target_kind: plan
            """
        );
        var plan = ParseConvention(
            """
            id: plan
            match:
              glob: "docs/PLANS/**/*.md"
            slug:
              from: filename
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { adr, plan });
        var refs0014 = result
            .Refs.Where(r =>
                r.FromEntityId
                == result.Entities.First(e => e.Kind == "adr" && e.Slug == "0014-foo").Id
            )
            .ToList();

        Assert.Equal(2, refs0014.Count); // one per filter
        Assert.Contains(
            refs0014,
            r => r.Relationship == "related_adrs" && r.ToEntityId is not null
        );
        Assert.Contains(
            refs0014,
            r => r.Relationship == "related_plans" && r.ToEntityId is not null
        );
    }

    [Fact]
    public async Task Scan_InverseEdge_AutoEmittedAlongsideForwardEdge()
    {
        await SeedFile("docs/ADR/0001-old.md", "---\ntitle: Old\n---\n");
        await SeedFile("docs/ADR/0002-new.md", "---\ntitle: New\nsupersedes:\n  - 0001-old\n---\n");

        var adr = ParseConvention(
            """
            id: adr
            match:
              glob: "docs/ADR/**/*.md"
            slug:
              from: filename
            relationships:
              - kind: supersedes
                name: Supersedes
                source: frontmatter.supersedes
                interpret: slug
                target_kind: adr
                inverse: superseded_by
                inverse_name: Superseded by
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { adr });
        var newAdr = result.Entities.First(e => e.Slug == "0002-new");
        var oldAdr = result.Entities.First(e => e.Slug == "0001-old");

        var forward = result.Refs.First(r =>
            r.FromEntityId == newAdr.Id && r.Relationship == "supersedes"
        );
        Assert.Equal(oldAdr.Id, forward.ToEntityId);

        var reverse = result.Refs.First(r =>
            r.FromEntityId == oldAdr.Id && r.Relationship == "superseded_by"
        );
        Assert.Equal(newAdr.Id, reverse.ToEntityId);
    }

    [Fact]
    public async Task Scan_TargetKindAny_ResolvesAcrossKinds()
    {
        await SeedFile("docs/ADR/foo.md", "---\ntitle: Foo\n---\n");
        await SeedFile("docs/PLANS/bar.md", "---\ntitle: Bar\n---\n");
        await SeedFile(
            "docs/ADR/index.md",
            "---\ntitle: Index\nrelated:\n  - docs/ADR/foo.md\n  - docs/PLANS/bar.md\n---\n"
        );

        var adr = ParseConvention(
            """
            id: adr
            priority: 100
            match:
              glob: "docs/ADR/**/*.md"
            slug:
              from: filename
            relationships:
              - kind: related
                name: Related
                source: frontmatter.related
                interpret: path
                target_kind: any
            """
        );
        var plan = ParseConvention(
            """
            id: plan
            priority: 90
            match:
              glob: "docs/PLANS/**/*.md"
            slug:
              from: filename
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { adr, plan });
        var indexAdr = result.Entities.First(e => e.Kind == "adr" && e.Slug == "index");
        var refs = result
            .Refs.Where(r => r.FromEntityId == indexAdr.Id && r.Relationship == "related")
            .ToList();

        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.NotNull(r.ToEntityId));
        Assert.Contains(refs, r => r.TargetKind == "adr");
        Assert.Contains(refs, r => r.TargetKind == "plan");
    }

    [Fact]
    public async Task Scan_BodyLinks_ExtractsMarkdownLinks()
    {
        await SeedFile("docs/peer.md", "---\ntitle: Peer\n---\n");
        await SeedFile(
            "docs/source.md",
            "---\ntitle: Source\n---\n# Heading\n\nSee [the peer](docs/peer.md) for details.\nAnother [external](https://example.com/a).\n\n[ref]: docs/peer.md\n"
        );

        var convention = ParseConvention(
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            relationships:
              - kind: body_links
                name: Body Links
                source: body.links
                interpret: auto
                target_kind: any
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });
        var sourceEntity = result.Entities.First(e => e.Slug == "source");
        var refs = result.Refs.Where(r => r.FromEntityId == sourceEntity.Id).ToList();

        // Three links extracted: docs/peer.md (inline), https://example.com/a (inline external), docs/peer.md (reference-style).
        Assert.Equal(3, refs.Count);

        // Two of them should resolve to the peer entity.
        var resolvedToPeer = refs.Where(r => r.ToEntityId is not null).ToList();
        Assert.Equal(2, resolvedToPeer.Count);

        // One should be the external URL (unresolved with metadata.kind=url).
        var urlRef = refs.First(r =>
            r.MetadataJson is not null
            && r.MetadataJson.Contains("\"url\":\"https://example.com/a\"")
        );
        var urlMeta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            urlRef.MetadataJson!
        )!;
        Assert.Equal("url", urlMeta["kind"].GetString());
    }

    [Fact]
    public async Task Scan_BodyCodeRefs_ExtractsInlineCodePaths()
    {
        await SeedFile(
            "docs/adr.md",
            "---\ntitle: ADR\n---\n\nThe relevant code lives in `packages/db/repo.ts:42` and `src/UserService.cs`.\n\nIgnore short backticks like `foo` and dotted symbols like `Foo.Bar`. Only paths with slashes match.\n"
        );

        var convention = ParseConvention(
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            relationships:
              - kind: code_refs
                name: Code Refs
                source: body.code_refs
                interpret: path
                target_kind: any
            """
        );
        var result = _scanner.Scan(_workspace, _root, new[] { convention });
        var entity = result.Entities.Single();
        var refs = result.Refs.Where(r => r.FromEntityId == entity.Id).ToList();

        // Two paths extracted; bare identifiers ignored.
        Assert.Equal(2, refs.Count);
        // Both unresolved (no convention matches the .ts/.cs files).
        Assert.All(refs, r => Assert.Null(r.ToEntityId));

        // The `:42` line suffix is stripped — code-ref lookups happen on the
        // path component alone. The raw metadata records the path without the
        // line; line-aware navigation is a downstream concern.
        var paths = refs.Select(r => r.TargetSlug ?? string.Empty).ToList();
        Assert.Contains("repo", paths); // stem of packages/db/repo.ts
        Assert.Contains("UserService", paths); // stem of src/UserService.cs
    }

    private async Task SeedFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private static Convention ParseConvention(string yaml)
    {
        var (c, err) = ConventionLoader.Parse(yaml, sourcePath: null);
        if (err is not null)
            throw new InvalidOperationException(err.Message);
        return c!;
    }
}
