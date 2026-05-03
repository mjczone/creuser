using Creuser.Core.Execution;

namespace Creuser.Core.Tests;

public class ExecutionShapeTests
{
    [Theory]
    [InlineData("deterministic", true)]
    [InlineData("plan-then-execute", true)]
    [InlineData("agentic", true)]
    [InlineData("DETERMINISTIC", false)]
    [InlineData("Deterministic", false)]
    [InlineData("magic", false)]
    [InlineData("", false)]
    public void JobPattern_IsValid_AcceptsExactlyKnownValues(string input, bool expected)
    {
        Assert.Equal(expected, JobPattern.IsValid(input));
    }

    [Theory]
    [InlineData("draft", true)]
    [InlineData("active", true)]
    [InlineData("disabled", true)]
    [InlineData("paused", false)]
    [InlineData("", false)]
    public void JobScriptStatus_IsValid_AcceptsExactlyKnownValues(string input, bool expected)
    {
        Assert.Equal(expected, JobScriptStatus.IsValid(input));
    }

    [Fact]
    public void StepResult_Success_FactoryProducesSucceededWithEmptyChanges()
    {
        var outputs = new Dictionary<string, object?> { ["text"] = "hello", ["tokens_used"] = 12L };

        var result = StepResult.Success(outputs, durationMs: 250, tokensUsed: 12, costUsd: 0.001m);

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal("hello", result.Outputs["text"]);
        Assert.Equal(12L, result.TokensUsed);
        Assert.Equal(0.001m, result.CostUsd);
        Assert.Empty(result.FileChanges);
        Assert.Empty(result.Artifacts);
        Assert.Equal(250, result.DurationMs);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.ResumeToken);
    }

    [Fact]
    public void StepResult_Failure_FactoryProducesFailedWithMessageAndNoOutputs()
    {
        var result = StepResult.Failure("auth rejected", durationMs: 100);

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal("auth rejected", result.ErrorMessage);
        Assert.Empty(result.Outputs);
        Assert.Empty(result.FileChanges);
        Assert.Empty(result.Artifacts);
        Assert.Equal(100, result.DurationMs);
    }

    [Fact]
    public void FileChange_RecordEqualityUsesStructuralEquality()
    {
        var a = new FileChange(
            "docs/index.md",
            FileChangeOp.Modify,
            BeforeHash: "abc",
            AfterHash: "def"
        );
        var b = new FileChange(
            "docs/index.md",
            FileChangeOp.Modify,
            BeforeHash: "abc",
            AfterHash: "def"
        );

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void FileChange_DifferentPaths_AreNotEqual()
    {
        var a = new FileChange("docs/a.md", FileChangeOp.Create, AfterHash: "x");
        var b = new FileChange("docs/b.md", FileChangeOp.Create, AfterHash: "x");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StepBudgets_AllowsAllNullsForInheritFromRunSemantics()
    {
        var budgets = new StepBudgets();

        Assert.Null(budgets.MaxDuration);
        Assert.Null(budgets.MaxTokens);
        Assert.Null(budgets.MaxCostUsd);
    }

    [Fact]
    public void StepResult_RecordWith_PreservesUnchangedFields()
    {
        var original = StepResult.Success(
            new Dictionary<string, object?> { ["k"] = "v" },
            durationMs: 50
        );

        var withResume = original with { Status = StepStatus.Paused, ResumeToken = "wait-123" };

        Assert.Equal(StepStatus.Paused, withResume.Status);
        Assert.Equal("wait-123", withResume.ResumeToken);
        Assert.Equal("v", withResume.Outputs["k"]);
        Assert.Equal(50, withResume.DurationMs);
    }
}
