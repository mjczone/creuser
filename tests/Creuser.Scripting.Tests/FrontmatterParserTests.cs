using Creuser.Scripting;

namespace Creuser.Scripting.Tests;

public class FrontmatterParserTests
{
    [Fact]
    public void Split_NoFrontmatter_ReturnsBodyOnly()
    {
        var result = FrontmatterParser.Split("hello world\nno frontmatter here");

        Assert.Equal("", result.Frontmatter);
        Assert.Equal("hello world\nno frontmatter here", result.Body);
    }

    [Fact]
    public void Split_EmptyInput_ReturnsEmpty()
    {
        var result = FrontmatterParser.Split("");

        Assert.Equal("", result.Frontmatter);
        Assert.Equal("", result.Body);
    }

    [Fact]
    public void Split_BasicFrontmatter_SeparatesCorrectly()
    {
        var raw = "---\ntype: llm-chat\nname: Test\n---\nbody content here";

        var result = FrontmatterParser.Split(raw);

        Assert.Contains("type: llm-chat", result.Frontmatter);
        Assert.Contains("name: Test", result.Frontmatter);
        Assert.Equal("body content here", result.Body);
    }

    [Fact]
    public void Split_FrontmatterWithEmbeddedDashes_DoesNotPrematurelyTerminate()
    {
        // Three dashes embedded mid-line should NOT close the frontmatter
        // — the closer must be `---` on its own line.
        var raw = "---\nname: foo --- bar\ntype: llm-chat\n---\nactual body";

        var result = FrontmatterParser.Split(raw);

        Assert.Contains("name: foo --- bar", result.Frontmatter);
        Assert.Contains("type: llm-chat", result.Frontmatter);
        Assert.Equal("actual body", result.Body);
    }

    [Fact]
    public void Split_UnterminatedFrontmatter_Throws()
    {
        var raw = "---\ntype: llm-chat\nname: Test\nno closing delimiter";

        var ex = Assert.Throws<FrontmatterParseException>(() => FrontmatterParser.Split(raw));
        Assert.Contains("Unterminated", ex.Message);
    }

    [Fact]
    public void Split_NormalizesCrlfLineEndings()
    {
        // Authors saving via Windows editors deliver \r\n; the parser must
        // round-trip them as LF without breaking the delimiter detection.
        var raw = "---\r\ntype: llm-chat\r\n---\r\nbody";

        var result = FrontmatterParser.Split(raw);

        Assert.Contains("type: llm-chat", result.Frontmatter);
        Assert.Equal("body", result.Body);
    }

    [Fact]
    public void ParseFrontmatter_EmptyString_ReturnsDefaults()
    {
        var result = FrontmatterParser.ParseFrontmatter("");

        Assert.Equal("llm-chat", result.Type);
        Assert.Equal("deterministic", result.Pattern);
        Assert.Empty(result.Inputs);
        Assert.Empty(result.RequiredSecrets);
    }

    [Fact]
    public void ParseFrontmatter_BasicFields_PopulatesType()
    {
        var yaml = "type: shell\npattern: deterministic\n";

        var result = FrontmatterParser.ParseFrontmatter(yaml);

        Assert.Equal("shell", result.Type);
        Assert.Equal("deterministic", result.Pattern);
    }

    [Fact]
    public void ParseFrontmatter_NestedInputs_DeserializesAsDictionary()
    {
        var yaml = """
            type: llm-chat
            inputs:
              temperature: 0.5
              system_prompt: You are helpful.
            """;

        var result = FrontmatterParser.ParseFrontmatter(yaml);

        Assert.Equal("llm-chat", result.Type);
        Assert.Equal(2, result.Inputs.Count);
        Assert.Contains("temperature", result.Inputs.Keys);
        Assert.Contains("system_prompt", result.Inputs.Keys);
    }

    [Fact]
    public void ParseFrontmatter_RequiredSecretsAndAllowedCommands_PopulateLists()
    {
        var yaml = """
            type: shell
            required_secrets:
              - anthropic.key
            allowed_commands:
              - git
              - rg
              - fd
            """;

        var result = FrontmatterParser.ParseFrontmatter(yaml);

        Assert.Single(result.RequiredSecrets);
        Assert.Equal("anthropic.key", result.RequiredSecrets[0]);
        Assert.Equal(3, result.AllowedCommands.Count);
        Assert.Contains("git", result.AllowedCommands);
    }

    [Fact]
    public void ParseFrontmatter_BudgetsBlock_DeserializesNestedNumbers()
    {
        var yaml = """
            type: llm-chat
            budgets:
              max_duration_seconds: 600
              max_tokens: 50000
              max_cost_usd: 0.50
            """;

        var result = FrontmatterParser.ParseFrontmatter(yaml);

        Assert.NotNull(result.Budgets);
        Assert.Equal(600, result.Budgets!.MaxDurationSeconds);
        Assert.Equal(50000, result.Budgets.MaxTokens);
        Assert.Equal(0.50m, result.Budgets.MaxCostUsd);
    }

    [Fact]
    public void ParseFrontmatter_ScheduleBlock_DeserializesCronAndTriggers()
    {
        var yaml = """
            type: llm-chat
            schedule:
              cron: "0 6 * * *"
              trigger_on:
                - sync
                - manual
            """;

        var result = FrontmatterParser.ParseFrontmatter(yaml);

        Assert.NotNull(result.Schedule);
        Assert.Equal("0 6 * * *", result.Schedule!.Cron);
        Assert.Equal(2, result.Schedule.TriggerOn.Count);
        Assert.Contains("sync", result.Schedule.TriggerOn);
    }

    [Fact]
    public void ParseFrontmatter_MalformedYaml_ThrowsTypedException()
    {
        var yaml = "type: llm-chat\n  bad indentation: here\n   wrong";

        Assert.Throws<FrontmatterParseException>(() => FrontmatterParser.ParseFrontmatter(yaml));
    }

    [Fact]
    public void ParseFrontmatter_MultiStepDag_DeserializesSteps()
    {
        var yaml = """
            pattern: deterministic
            steps:
              - id: fetch
                type: http
                inputs:
                  url: https://example.com/feed.xml
              - id: parse
                type: llm-chat
                depends_on:
                  - fetch
                inputs:
                  prompt: "Extract titles."
                  input: $fetch.body
              - id: write
                type: file-mutate
                depends_on:
                  - parse
                inputs:
                  ops:
                    - op: create
                      path: out.json
                      content: $parse.text
            """;

        var result = FrontmatterParser.ParseFrontmatter(yaml);

        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("fetch", result.Steps[0].Id);
        Assert.Equal("http", result.Steps[0].Type);
        Assert.Equal("parse", result.Steps[1].Id);
        Assert.Equal(new List<string> { "fetch" }, result.Steps[1].DependsOn);
        Assert.Equal("$fetch.body", result.Steps[1].Inputs["input"]);
        Assert.Equal("write", result.Steps[2].Id);
    }

    [Fact]
    public void ParseFrontmatter_UnknownProperties_AreIgnored()
    {
        // The parser must tolerate unknown keys so future frontmatter additions
        // don't break older deployments. The deserializer is configured to
        // ignore unmatched properties.
        var yaml = """
            type: llm-chat
            future_field_we_dont_know_yet: true
            another_unknown: { nested: value }
            """;

        var result = FrontmatterParser.ParseFrontmatter(yaml);

        Assert.Equal("llm-chat", result.Type);
    }
}
