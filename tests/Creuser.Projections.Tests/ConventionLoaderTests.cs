using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Projections.Conventions;

namespace Creuser.Projections.Tests;

public class ConventionLoaderTests : IAsyncLifetime
{
    private string _root = null!;
    private Workspace _workspace = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"creuser-cl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".creuser", "conventions"));
        _workspace = new Workspace(
            Id: Guid.NewGuid(),
            Slug: "test",
            Name: "Test",
            Description: null,
            Type: "local",
            Settings: "{}",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            CreatedBy: null
        );
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
    public async Task Load_NoConventionsDirectory_ReturnsEmpty()
    {
        var bareRoot = Path.Combine(Path.GetTempPath(), $"creuser-bare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bareRoot);
        try
        {
            var loader = new ConventionLoader();
            var result = await loader.LoadAsync(_workspace, bareRoot, CancellationToken.None);
            Assert.Empty(result.Conventions);
            Assert.Empty(result.Errors);
        }
        finally
        {
            Directory.Delete(bareRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Load_MinimalConvention_ParsesCorrectly()
    {
        await WriteConvention(
            "minimal.yaml",
            """
            id: my_kind
            description: Minimal example.
            match:
              glob: "**/*.md"
            slug:
              from: filename
            """
        );

        var loader = new ConventionLoader();
        var result = await loader.LoadAsync(_workspace, _root, CancellationToken.None);
        Assert.Single(result.Conventions);
        Assert.Empty(result.Errors);
        var c = result.Conventions[0];
        Assert.Equal("my_kind", c.Id);
        Assert.Equal("Minimal example.", c.Description);
        Assert.Equal("**/*.md", c.Match.Glob);
        Assert.Equal("filename", c.Slug.From);
        Assert.Equal("frontmatter", c.Metadata.Source); // default
    }

    [Fact]
    public async Task Load_ExtendsStandard_MergesBaseFields()
    {
        await WriteConvention(
            "rules.yaml",
            """
            id: business_rule
            extends: creuser:standard/business-rule
            priority: 200
            """
        );

        var loader = new ConventionLoader();
        var result = await loader.LoadAsync(_workspace, _root, CancellationToken.None);
        Assert.Single(result.Conventions);
        var c = result.Conventions[0];
        Assert.Equal("business_rule", c.Id);
        Assert.Equal(200, c.Priority);
        // Glob inherited from base.
        Assert.Equal("business-rules/**/*.md", c.Match.Glob);
        // Relationships inherited from base.
        Assert.NotEmpty(c.Relationships);
    }

    [Fact]
    public async Task Load_UnknownExtends_RecordsError()
    {
        await WriteConvention(
            "broken.yaml",
            """
            id: x
            extends: creuser:standard/nonexistent
            """
        );

        var loader = new ConventionLoader();
        var result = await loader.LoadAsync(_workspace, _root, CancellationToken.None);
        Assert.Empty(result.Conventions);
        Assert.Single(result.Errors);
        Assert.Contains("Unknown extends target", result.Errors[0].Message);
    }

    [Fact]
    public async Task Load_MalformedYaml_RecordsError()
    {
        await WriteConvention("bad.yaml", "this: : is::: not yaml: :: :");

        var loader = new ConventionLoader();
        var result = await loader.LoadAsync(_workspace, _root, CancellationToken.None);
        Assert.Empty(result.Conventions);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Load_FileStartingWithUnderscore_IsSkipped()
    {
        await WriteConvention(
            "_partial.yaml",
            """
            id: partial
            match:
              glob: "**/*.md"
            slug:
              from: filename
            """
        );

        var loader = new ConventionLoader();
        var result = await loader.LoadAsync(_workspace, _root, CancellationToken.None);
        Assert.Empty(result.Conventions);
    }

    [Fact]
    public async Task Load_ContentHash_IsStableAcrossLoads()
    {
        await WriteConvention(
            "stable.yaml",
            """
            id: stable
            match:
              glob: "**/*.md"
            slug:
              from: filename
            """
        );

        var loader = new ConventionLoader();
        var first = await loader.LoadAsync(_workspace, _root, CancellationToken.None);
        var second = await loader.LoadAsync(_workspace, _root, CancellationToken.None);
        Assert.Equal(first.Conventions[0].ContentHash, second.Conventions[0].ContentHash);
    }

    private async Task WriteConvention(string fileName, string yaml) =>
        await File.WriteAllTextAsync(
            Path.Combine(_root, ".creuser", "conventions", fileName),
            yaml
        );
}
