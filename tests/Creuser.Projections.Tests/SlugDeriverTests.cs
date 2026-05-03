using Creuser.Core.Projections;
using Creuser.Projections.Scanner;

namespace Creuser.Projections.Tests;

public class SlugDeriverTests
{
    [Fact]
    public void From_Filename_StripsExtension()
    {
        var spec = new ConventionSlugSpec("filename", "as-is", null);
        Assert.Equal("login", SlugDeriver.Derive(spec, "business-rules/auth/login.md", null));
    }

    [Fact]
    public void From_Filename_AppliesKebabTransform()
    {
        var spec = new ConventionSlugSpec("filename", "kebab", null);
        Assert.Equal(
            "login-rule",
            SlugDeriver.Derive(spec, "business-rules/auth/Login_Rule.md", null)
        );
    }

    [Fact]
    public void From_Filename_AppliesSnakeTransform()
    {
        var spec = new ConventionSlugSpec("filename", "snake", null);
        Assert.Equal(
            "login_rule",
            SlugDeriver.Derive(spec, "business-rules/auth/Login-Rule.md", null)
        );
    }

    [Fact]
    public void From_Path_DropsExtensionAndJoinsWithDashes()
    {
        var spec = new ConventionSlugSpec("path", "as-is", null);
        Assert.Equal(
            "business-rules-auth-login",
            SlugDeriver.Derive(spec, "business-rules/auth/login.md", null)
        );
    }

    [Fact]
    public void From_Frontmatter_ReadsKeyValue()
    {
        var spec = new ConventionSlugSpec("frontmatter.id", "as-is", null);
        var fm = new Dictionary<string, object?> { ["id"] = "explicit-slug" };
        Assert.Equal("explicit-slug", SlugDeriver.Derive(spec, "any.md", fm));
    }

    [Fact]
    public void From_Frontmatter_MissingKey_Throws()
    {
        var spec = new ConventionSlugSpec("frontmatter.id", "as-is", null);
        Assert.Throws<InvalidOperationException>(() => SlugDeriver.Derive(spec, "x.md", null));
    }

    [Fact]
    public void From_Template_InterpolatesVariables()
    {
        var spec = new ConventionSlugSpec("template", "kebab", "{parent_dir}-{filename}");
        Assert.Equal("auth-login", SlugDeriver.Derive(spec, "business-rules/auth/login.md", null));
    }

    [Fact]
    public void From_Template_UnknownVariable_Throws()
    {
        var spec = new ConventionSlugSpec("template", "as-is", "{missing}");
        Assert.Throws<InvalidOperationException>(() => SlugDeriver.Derive(spec, "x.md", null));
    }
}
