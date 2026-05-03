using Creuser.Scripting;

namespace Creuser.Scripting.Tests;

public class FrontmatterDialectTests
{
    [Theory]
    [InlineData("docs/intro.md", DialectKind.Markdown)]
    [InlineData("docs/intro.MD", DialectKind.Markdown)]
    [InlineData("README.markdown", DialectKind.Markdown)]
    [InlineData("page.mdx", DialectKind.Markdown)]
    [InlineData("page.astro", DialectKind.Markdown)]
    [InlineData("src/foo.ts", DialectKind.CStyle)]
    [InlineData("src/foo.tsx", DialectKind.CStyle)]
    [InlineData("src/foo.js", DialectKind.CStyle)]
    [InlineData("src/foo.cs", DialectKind.CStyle)]
    [InlineData("src/main.go", DialectKind.CStyle)]
    [InlineData("src/main.rs", DialectKind.CStyle)]
    [InlineData("scripts/build.py", DialectKind.Hash)]
    [InlineData("scripts/build.sh", DialectKind.Hash)]
    [InlineData("config/app.yaml", DialectKind.Hash)]
    [InlineData("config/app.toml", DialectKind.Hash)]
    [InlineData("Dockerfile.dockerfile", DialectKind.Hash)]
    [InlineData("public/index.html", DialectKind.Html)]
    [InlineData("page.htm", DialectKind.Html)]
    [InlineData("App.vue", DialectKind.Html)]
    [InlineData("queries/find.sql", DialectKind.SqlDash)]
    public void FromPath_RecognizedExtension_ReturnsCorrectDialect(
        string path,
        DialectKind expected
    )
    {
        var dialect = FrontmatterDialects.FromPath(path);
        Assert.NotNull(dialect);
        Assert.Equal(expected, dialect!.Kind);
    }

    [Theory]
    [InlineData("file-without-extension")]
    [InlineData("photo.png")]
    [InlineData("video.mp4")]
    [InlineData("data.bin")]
    public void FromPath_UnrecognizedExtension_ReturnsNull(string path)
    {
        Assert.Null(FrontmatterDialects.FromPath(path));
    }

    [Fact]
    public void Markdown_DialectMetadata_MatchesConvention()
    {
        var d = FrontmatterDialects.Markdown;
        Assert.Equal("---", d.Opener);
        Assert.Equal("---", d.Closer);
        Assert.Equal("", d.LinePrefix);
        Assert.False(d.SupportsShebang);
    }

    [Fact]
    public void Hash_DialectMetadata_SupportsShebang()
    {
        var d = FrontmatterDialects.Hash;
        Assert.Equal("# ---", d.Opener);
        Assert.Equal("# ---", d.Closer);
        Assert.Equal("# ", d.LinePrefix);
        Assert.True(d.SupportsShebang);
    }

    [Fact]
    public void CStyle_DialectMetadata_BlockComment()
    {
        var d = FrontmatterDialects.CStyle;
        Assert.Equal("/* ---", d.Opener);
        Assert.Equal("--- */", d.Closer);
        Assert.Equal("", d.LinePrefix);
    }
}
