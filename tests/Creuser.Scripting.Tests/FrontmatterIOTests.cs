using Creuser.Scripting;

namespace Creuser.Scripting.Tests;

public class FrontmatterIOTests
{
    [Fact]
    public void Find_NoBlockInMarkdown_ReturnsNotExisted()
    {
        var content = "# A heading\n\nBody text.";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.Markdown);
        Assert.False(found.Existed);
        Assert.Equal(-1, found.OpenerLineIndex);
    }

    [Fact]
    public void Find_BlockAtTopOfMarkdown_ReturnsPayload()
    {
        var content = "---\ntitle: Foo\ntags:\n  - a\n  - b\n---\n\nBody.";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.Markdown);
        Assert.True(found.Existed);
        Assert.Equal(0, found.OpenerLineIndex);
        Assert.Equal(5, found.CloserLineIndex);
        Assert.Contains("title: Foo", found.YamlPayload);
    }

    [Fact]
    public void Find_BlockAfterShebangInPython_HonorsShebangPosition()
    {
        var content = "#!/usr/bin/env python3\n# ---\n# title: Foo\n# ---\nimport os\n";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.Hash);
        Assert.True(found.Existed);
        Assert.Equal(0, found.ShebangLineIndex);
        Assert.Equal(1, found.OpenerLineIndex);
        Assert.Equal(3, found.CloserLineIndex);
        Assert.Contains("title: Foo", found.YamlPayload);
    }

    [Fact]
    public void Find_NoBlockButShebang_RecordsShebangLine()
    {
        var content = "#!/usr/bin/env bash\n\necho hi\n";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.Hash);
        Assert.False(found.Existed);
        Assert.Equal(0, found.ShebangLineIndex);
    }

    [Fact]
    public void Find_CStyleBlockInTypescript_StripsCommentWrapper()
    {
        var content = "/* ---\ntitle: Foo\ncategory: domain\n--- */\n\nexport const x = 1;\n";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.CStyle);
        Assert.True(found.Existed);
        Assert.Contains("title: Foo", found.YamlPayload);
        Assert.Contains("category: domain", found.YamlPayload);
    }

    [Fact]
    public void Find_HashBlockInPython_StripsLinePrefix()
    {
        var content = "# ---\n# title: Foo\n# tags:\n#   - api\n# ---\nimport os\n";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.Hash);
        Assert.True(found.Existed);
        // Payload should NOT contain the `# ` prefix.
        Assert.DoesNotContain("# title", found.YamlPayload);
        Assert.Contains("title: Foo", found.YamlPayload);
        Assert.Contains("- api", found.YamlPayload);
    }

    [Fact]
    public void Find_HtmlBlock_StripsHtmlComment()
    {
        var content = "<!-- ---\ntitle: Foo\n--- -->\n<html>";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.Html);
        Assert.True(found.Existed);
        Assert.Contains("title: Foo", found.YamlPayload);
    }

    [Fact]
    public void Find_SqlDashBlock_StripsDashComment()
    {
        var content = "-- ---\n-- title: Find users\n-- ---\nSELECT * FROM users;";
        var found = FrontmatterIO.Find(content, FrontmatterDialects.SqlDash);
        Assert.True(found.Existed);
        Assert.Contains("title: Find users", found.YamlPayload);
    }

    [Fact]
    public void ParsePayload_ValidYaml_ReturnsDictionary()
    {
        var values = FrontmatterIO.ParsePayload("title: Foo\ncategory: core\n");
        Assert.Equal("Foo", values["title"]);
        Assert.Equal("core", values["category"]);
    }

    [Fact]
    public void ParsePayload_NestedYaml_ReturnsNestedDict()
    {
        var yaml = "owner:\n  name: alice\n  team: a\ntags:\n  - one\n  - two\n";
        var values = FrontmatterIO.ParsePayload(yaml);
        var owner = (Dictionary<string, object?>)values["owner"]!;
        Assert.Equal("alice", owner["name"]);
        var tags = (List<object?>)values["tags"]!;
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void ParsePayload_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(FrontmatterIO.ParsePayload(""));
        Assert.Empty(FrontmatterIO.ParsePayload("   \n  \n"));
    }

    [Fact]
    public void SerializeBlock_Markdown_BareYamlBetweenDashes()
    {
        var values = new Dictionary<string, object?> { ["title"] = "Foo", ["category"] = "core" };
        var block = FrontmatterIO.SerializeBlock(values, FrontmatterDialects.Markdown);
        Assert.StartsWith("---\n", block);
        Assert.EndsWith("---\n", block);
        Assert.Contains("title: Foo", block);
        Assert.DoesNotContain("# ", block); // no line prefix
    }

    [Fact]
    public void SerializeBlock_Hash_PrefixesEveryLine()
    {
        var values = new Dictionary<string, object?> { ["title"] = "Foo" };
        var block = FrontmatterIO.SerializeBlock(values, FrontmatterDialects.Hash);
        Assert.Contains("# ---\n", block);
        Assert.Contains("# title: Foo", block);
    }

    [Fact]
    public void SerializeBlock_CStyle_BlockComment()
    {
        var values = new Dictionary<string, object?> { ["title"] = "Foo" };
        var block = FrontmatterIO.SerializeBlock(values, FrontmatterDialects.CStyle);
        Assert.StartsWith("/* ---\n", block);
        Assert.EndsWith("--- */\n", block);
        Assert.Contains("title: Foo", block);
    }

    [Fact]
    public void Splice_InsertsIntoFileWithoutExistingBlock()
    {
        var content = "# Existing heading\n\nBody.\n";
        var dialect = FrontmatterDialects.Markdown;
        var found = FrontmatterIO.Find(content, dialect);
        var block = FrontmatterIO.SerializeBlock(
            new Dictionary<string, object?> { ["title"] = "Added" },
            dialect
        );
        var result = FrontmatterIO.Splice(content, dialect, block, found);

        Assert.StartsWith("---\n", result);
        Assert.Contains("title: Added", result);
        Assert.Contains("# Existing heading", result);
        Assert.Contains("Body.", result);
    }

    [Fact]
    public void Splice_PreservesShebangWhenInsertingInPython()
    {
        var content = "#!/usr/bin/env python3\nimport os\n";
        var dialect = FrontmatterDialects.Hash;
        var found = FrontmatterIO.Find(content, dialect);
        var block = FrontmatterIO.SerializeBlock(
            new Dictionary<string, object?> { ["title"] = "Foo" },
            dialect
        );
        var result = FrontmatterIO.Splice(content, dialect, block, found);

        Assert.StartsWith("#!/usr/bin/env python3\n", result);
        Assert.Contains("# ---", result);
        Assert.Contains("# title: Foo", result);
        Assert.Contains("import os", result);
    }

    [Fact]
    public void Splice_ReplacesExistingBlock_PreservesBodyExactly()
    {
        var content = "---\ntitle: Old\ncategory: legacy\n---\n\nBody text after.\nMore body.\n";
        var dialect = FrontmatterDialects.Markdown;
        var found = FrontmatterIO.Find(content, dialect);
        var block = FrontmatterIO.SerializeBlock(
            new Dictionary<string, object?> { ["title"] = "New", ["category"] = "core" },
            dialect
        );
        var result = FrontmatterIO.Splice(content, dialect, block, found);

        Assert.Contains("title: New", result);
        Assert.DoesNotContain("title: Old", result);
        Assert.Contains("Body text after.", result);
        Assert.Contains("More body.", result);
    }
}
