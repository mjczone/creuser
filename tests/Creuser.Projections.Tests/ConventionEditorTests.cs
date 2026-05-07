using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Projections.Accessors;
using Creuser.Projections.Authoring;
using Creuser.Projections.Conventions;
using Creuser.Projections.Scanner;

namespace Creuser.Projections.Tests;

public class ConventionEditorTests : IAsyncLifetime
{
    private string _root = null!;
    private Workspace _workspace = null!;
    private ConventionEditor _editor = null!;
    private FakeWorkingTree _tree = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"creuser-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".creuser", "conventions"));
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
        _tree = new FakeWorkingTree(_root);
        _editor = new ConventionEditor(_tree);
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
    public async Task AddRelationship_AppendsRuleAndPersists()
    {
        await WriteConvention(
            "doc.yaml",
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            """
        );

        var result = await _editor.AddRelationshipAsync(
            _workspace,
            "doc",
            new RelationshipEdit(
                Kind: "related",
                Name: "Related",
                Source: "frontmatter.related",
                Interpret: "auto",
                TargetKind: "any",
                Inverse: "related"
            )
        );
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Convention);
        var rel = result.Convention!.Relationships.Single();
        Assert.Equal("related", rel.Kind);
        Assert.Equal("Related", rel.Name);
        Assert.True(rel.TargetKind.Any);

        // The file on disk should round-trip cleanly through the loader again.
        var written = await File.ReadAllTextAsync(
            Path.Combine(_root, ".creuser", "conventions", "doc.yaml")
        );
        Assert.Contains("kind: related", written);
        Assert.Contains("source: frontmatter.related", written);
    }

    [Fact]
    public async Task AddRelationship_DuplicateKind_Fails()
    {
        await WriteConvention(
            "doc.yaml",
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            relationships:
              - kind: existing
                source: frontmatter.existing
                interpret: slug
                target_kind: doc
            """
        );

        var result = await _editor.AddRelationshipAsync(
            _workspace,
            "doc",
            new RelationshipEdit(
                Kind: "existing",
                Source: "frontmatter.existing",
                Interpret: "slug"
            )
        );
        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public async Task UpdateRelationship_ReplacesRule()
    {
        await WriteConvention(
            "doc.yaml",
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            relationships:
              - kind: related
                source: frontmatter.related
                interpret: slug
                target_kind: doc
            """
        );

        var result = await _editor.UpdateRelationshipAsync(
            _workspace,
            "doc",
            "related",
            new RelationshipEdit(
                Kind: "related",
                Name: "Related Items",
                Icon: "link",
                Source: "frontmatter.related",
                Interpret: "auto",
                TargetKind: "any"
            )
        );
        Assert.True(result.Succeeded);
        var rel = result.Convention!.Relationships.Single();
        Assert.Equal("Related Items", rel.Name);
        Assert.Equal("link", rel.Icon);
        Assert.True(rel.TargetKind.Any);
    }

    [Fact]
    public async Task UpdateRelationship_UnknownKind_Fails()
    {
        await WriteConvention(
            "doc.yaml",
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            """
        );

        var result = await _editor.UpdateRelationshipAsync(
            _workspace,
            "doc",
            "missing",
            new RelationshipEdit(Kind: "missing")
        );
        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task RemoveRelationship_DropsRule()
    {
        await WriteConvention(
            "doc.yaml",
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            relationships:
              - kind: related
                source: frontmatter.related
                interpret: slug
                target_kind: doc
              - kind: parent
                select_path: "{file_dir}/index.md"
                target_kind: doc
            """
        );
        var result = await _editor.RemoveRelationshipAsync(_workspace, "doc", "related");
        Assert.True(result.Succeeded);
        var kinds = result.Convention!.Relationships.Select(r => r.Kind).ToList();
        Assert.DoesNotContain("related", kinds);
        Assert.Contains("parent", kinds);
    }

    [Fact]
    public void Validate_MalformedYaml_ReturnsError()
    {
        var v = _editor.Validate("this: : is::: not yaml: :: :");
        Assert.False(v.IsValid);
        Assert.Single(v.Errors);
    }

    [Fact]
    public void Validate_MissingRequiredField_ReturnsError()
    {
        var v = _editor.Validate("description: just-a-description");
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Message.Contains("missing"));
    }

    [Fact]
    public async Task TestAsync_MatchedFile_ReturnsEntityAndRefs()
    {
        await WriteConvention(
            "doc.yaml",
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            relationships:
              - kind: related
                source: frontmatter.related
                interpret: slug
                target_kind: doc
            """
        );
        await WriteFile("docs/foo.md", "---\ntitle: Foo\nrelated:\n  - bar\n---\n");
        await WriteFile("docs/bar.md", "---\ntitle: Bar\n---\n");

        var loader = new ConventionLoader();
        var scanner = new ProjectionScanner(TimeProvider.System, ComputedAccessorRegistry.Default);
        var test = await _editor.TestAsync(_workspace, "doc", "docs/foo.md", loader, scanner);

        Assert.True(test.Matched);
        Assert.Equal("foo", test.Entity!.Slug);
        Assert.Single(test.Refs);
        Assert.Equal("related", test.Refs[0].Relationship);
        Assert.NotNull(test.Refs[0].ToEntityId); // bar resolves
    }

    [Fact]
    public async Task TestAsync_PathNotMatched_ReturnsFailure()
    {
        await WriteConvention(
            "doc.yaml",
            """
            id: doc
            match:
              glob: "docs/*.md"
            slug:
              from: filename
            """
        );
        await WriteFile("other/foo.md", "---\ntitle: Foo\n---\n");

        var loader = new ConventionLoader();
        var scanner = new ProjectionScanner(TimeProvider.System, ComputedAccessorRegistry.Default);
        var test = await _editor.TestAsync(_workspace, "doc", "other/foo.md", loader, scanner);
        Assert.False(test.Matched);
        Assert.Contains("not matched", test.Error);
    }

    private async Task WriteConvention(string fileName, string yaml) =>
        await File.WriteAllTextAsync(
            Path.Combine(_root, ".creuser", "conventions", fileName),
            yaml
        );

    private async Task WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private sealed class FakeWorkingTree : IWorkspaceWorkingTree
    {
        private readonly string _path;

        public FakeWorkingTree(string path)
        {
            _path = path;
        }

        public Task<string?> ResolvePathAsync(
            Workspace workspace,
            CancellationToken ct = default
        ) => Task.FromResult<string?>(_path);

        public Task<ApplyAndCommitResult> ApplyAndCommitAsync(
            Workspace workspace,
            string workingTreePath,
            IReadOnlyList<FileChange> changes,
            string commitMessage,
            CancellationToken ct = default
        ) => throw new NotImplementedException();

        public Task<string?> ResolveHeadShaAsync(
            Workspace workspace,
            string workingTreePath,
            CancellationToken ct = default
        ) => Task.FromResult<string?>(null);
    }
}
