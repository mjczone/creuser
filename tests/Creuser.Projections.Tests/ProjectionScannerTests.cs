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
