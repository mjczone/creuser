using Creuser.Scripting;

namespace Creuser.Scripting.Tests;

public class DagValidatorTests
{
    [Fact]
    public void Validate_EmptyList_Fails()
    {
        var result = DagValidator.Validate(Array.Empty<JobScriptStepDecl>());
        Assert.NotNull(result.Error);
        Assert.Contains("no steps", result.Error);
    }

    [Fact]
    public void Validate_SingleStep_NoDeps_ReturnsAuthoredOrder()
    {
        var step = new JobScriptStepDecl { Id = "a", Type = "shell" };
        var result = DagValidator.Validate(new[] { step });
        Assert.Null(result.Error);
        Assert.Equal(new[] { "a" }, result.Sorted.Select(s => s.Id));
    }

    [Fact]
    public void Validate_MissingId_Fails()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl { Id = "", Type = "shell" },
            }
        );
        Assert.Contains("missing required `id`", result.Error);
    }

    [Fact]
    public void Validate_MissingType_Fails()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl { Id = "a", Type = "" },
            }
        );
        Assert.Contains("missing required `type`", result.Error);
    }

    [Fact]
    public void Validate_DuplicateIds_Fails()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl { Id = "a", Type = "shell" },
                new JobScriptStepDecl { Id = "a", Type = "shell" },
            }
        );
        Assert.Contains("Duplicate step id 'a'", result.Error);
    }

    [Fact]
    public void Validate_UnknownDependency_Fails()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl
                {
                    Id = "b",
                    Type = "shell",
                    DependsOn = new() { "ghost" },
                },
            }
        );
        Assert.Contains("depends on unknown step 'ghost'", result.Error);
    }

    [Fact]
    public void Validate_SelfDependency_Fails()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl
                {
                    Id = "a",
                    Type = "shell",
                    DependsOn = new() { "a" },
                },
            }
        );
        Assert.Contains("depends on itself", result.Error);
    }

    [Fact]
    public void Validate_TwoStepCycle_Fails()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl
                {
                    Id = "a",
                    Type = "shell",
                    DependsOn = new() { "b" },
                },
                new JobScriptStepDecl
                {
                    Id = "b",
                    Type = "shell",
                    DependsOn = new() { "a" },
                },
            }
        );
        Assert.Contains("cycle", result.Error);
    }

    [Fact]
    public void Validate_LinearChain_OrdersCorrectly()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl
                {
                    Id = "c",
                    Type = "shell",
                    DependsOn = new() { "b" },
                },
                new JobScriptStepDecl { Id = "a", Type = "shell" },
                new JobScriptStepDecl
                {
                    Id = "b",
                    Type = "shell",
                    DependsOn = new() { "a" },
                },
            }
        );
        Assert.Null(result.Error);
        Assert.Equal(new[] { "a", "b", "c" }, result.Sorted.Select(s => s.Id));
    }

    [Fact]
    public void Validate_DiamondDag_HonorsTopological()
    {
        // a → b, a → c, b → d, c → d. Two valid topo orders depending on
        // tie-breaking. The validator preserves authored order on ties, so
        // b comes before c in the output (because authored that way).
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl { Id = "a", Type = "shell" },
                new JobScriptStepDecl
                {
                    Id = "b",
                    Type = "shell",
                    DependsOn = new() { "a" },
                },
                new JobScriptStepDecl
                {
                    Id = "c",
                    Type = "shell",
                    DependsOn = new() { "a" },
                },
                new JobScriptStepDecl
                {
                    Id = "d",
                    Type = "shell",
                    DependsOn = new() { "b", "c" },
                },
            }
        );
        Assert.Null(result.Error);
        var ids = result.Sorted.Select(s => s.Id).ToList();
        Assert.Equal(0, ids.IndexOf("a"));
        Assert.Equal(3, ids.IndexOf("d"));
        // b before c (authored order), both before d
        Assert.True(ids.IndexOf("b") < ids.IndexOf("c"));
        Assert.True(ids.IndexOf("c") < ids.IndexOf("d"));
    }

    [Fact]
    public void Validate_EmptyDependencyEntry_Fails()
    {
        var result = DagValidator.Validate(
            new[]
            {
                new JobScriptStepDecl
                {
                    Id = "a",
                    Type = "shell",
                    DependsOn = new() { "" },
                },
            }
        );
        Assert.Contains("empty entry in `depends_on`", result.Error);
    }
}
