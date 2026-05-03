using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Creuser.Scripting;

/// <summary>
/// Splits a job script's raw text into YAML frontmatter + body, and parses
/// the frontmatter into <see cref="JobScriptFrontmatter"/>. The DB stores
/// both halves separately to round-trip authored YAML through edits without
/// losing comments or ordering.
///
/// <para>
/// Frontmatter is delimited by <c>---</c> at the start and end of the YAML
/// block. Both must be on their own line. Content before the opening
/// delimiter is forbidden (we treat the file as starting with frontmatter
/// or being all-body); content after the closing delimiter is the body.
/// </para>
/// </summary>
public static class FrontmatterParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Split raw script text into frontmatter and body. If no frontmatter
    /// delimiter is present, <see cref="ParsedScript.Frontmatter"/> is empty
    /// and the whole text becomes the body.
    /// </summary>
    public static ParsedScript Split(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return new ParsedScript("", "");

        // Normalize line endings so the regex / split logic is stable across
        // editors. Internal representation is LF-only.
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        if (!text.StartsWith("---\n", StringComparison.Ordinal) && text != "---")
        {
            // No leading delimiter — treat the whole file as the body.
            return new ParsedScript("", text);
        }

        // Find the closing delimiter on its own line.
        var afterOpening = text["---\n".Length..];
        var endIdx = FindClosingDelimiter(afterOpening);
        if (endIdx < 0)
        {
            // Unterminated frontmatter — treat as a malformed script. Throw
            // a typed error the API can surface; the editor will highlight.
            throw new FrontmatterParseException(
                "Unterminated frontmatter — expected `---` on its own line to close the YAML block."
            );
        }

        var frontmatter = afterOpening[..endIdx];
        // After the closing delimiter, skip past either "\n---\n" or trailing "\n---".
        var afterClosing = afterOpening[(endIdx + "\n---".Length)..];
        if (afterClosing.StartsWith('\n'))
            afterClosing = afterClosing[1..];

        return new ParsedScript(frontmatter, afterClosing);
    }

    /// <summary>
    /// Parse a (possibly empty) frontmatter string into the typed shape.
    /// Empty input yields a default <see cref="JobScriptFrontmatter"/> with
    /// the deterministic pattern.
    /// </summary>
    public static JobScriptFrontmatter ParseFrontmatter(string frontmatter)
    {
        if (string.IsNullOrWhiteSpace(frontmatter))
            return new JobScriptFrontmatter();
        try
        {
            return Deserializer.Deserialize<JobScriptFrontmatter>(frontmatter)
                ?? new JobScriptFrontmatter();
        }
        catch (Exception ex)
        {
            throw new FrontmatterParseException("YAML parse error: " + ex.Message, ex);
        }
    }

    private static int FindClosingDelimiter(string text)
    {
        // Closing delimiter is "---" at the start of a line, with either the
        // string ending immediately after or a newline following.
        var idx = 0;
        while (idx < text.Length)
        {
            var nl = text.IndexOf('\n', idx);
            var lineEnd = nl < 0 ? text.Length : nl;
            var line = text[idx..lineEnd];
            if (line == "---")
                return idx == 0 ? 0 : idx - 1; // include trailing \n of prior line in "frontmatter"
            if (nl < 0)
                break;
            idx = nl + 1;
        }
        return -1;
    }
}

public sealed record ParsedScript(string Frontmatter, string Body);

/// <summary>
/// Typed representation of frontmatter. Two shapes are supported:
/// <list type="bullet">
///   <item><b>Single-step</b> — top-level <see cref="Type"/> + body. The body becomes the step's content (prompt for llm-chat, script for shell/csharp/python/node). <see cref="Inputs"/> carries any additional configuration.</item>
///   <item><b>Multi-step</b> — top-level <see cref="Steps"/> array, each entry a <see cref="JobScriptStepDecl"/>. Steps reference each other via <c>depends_on</c>, and inputs can reference upstream outputs via <c>$step_id.output_name</c> bindings. The body is documentation only when steps are declared.</item>
/// </list>
/// </summary>
public sealed class JobScriptFrontmatter
{
    /// <summary>For single-step jobs: the runner type. Ignored when <see cref="Steps"/> is non-empty (each step declares its own type).</summary>
    public string Type { get; set; } = "llm-chat";

    /// <summary>One of <c>deterministic</c>, <c>plan-then-execute</c>, <c>agentic</c>. Defaults to deterministic.</summary>
    public string Pattern { get; set; } = "deterministic";

    /// <summary>For single-step jobs: runner-specific inputs. Ignored when <see cref="Steps"/> is non-empty (each step declares its own inputs).</summary>
    public Dictionary<string, object?> Inputs { get; set; } = new();

    /// <summary>Free-text declaration of secret filenames the runner may read via SecretsService.</summary>
    public List<string> RequiredSecrets { get; set; } = new();

    /// <summary>Per-job command allow-list for shell-type runners.</summary>
    public List<string> AllowedCommands { get; set; } = new();

    /// <summary>Budgets the executor enforces.</summary>
    public BudgetsBlock? Budgets { get; set; }

    /// <summary>Optional schedule. v0.1 doesn't run a scheduler yet, but we round-trip the value.</summary>
    public ScheduleBlock? Schedule { get; set; }

    /// <summary>Multi-step DAG declaration. Empty means "single-step job" (use <see cref="Type"/> + body). Non-empty switches the executor into DAG mode.</summary>
    public List<JobScriptStepDecl> Steps { get; set; } = new();

    public sealed class BudgetsBlock
    {
        public int? MaxDurationSeconds { get; set; }
        public long? MaxTokens { get; set; }
        public decimal? MaxCostUsd { get; set; }
    }

    public sealed class ScheduleBlock
    {
        public string? Cron { get; set; }
        public List<string> TriggerOn { get; set; } = new();
    }
}

/// <summary>
/// One step in a multi-step job's DAG. Steps execute in topological order
/// of their <see cref="DependsOn"/> edges; outputs flow forward via
/// <c>$step_id.field</c> bindings inside subsequent steps' inputs.
/// </summary>
public sealed class JobScriptStepDecl
{
    /// <summary>Stable identifier within the job. Must be unique. Used as the binding namespace (<c>$id.output</c>) and as the audit position label.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Optional human-readable name for the audit UI. Defaults to <see cref="Id"/> when absent.</summary>
    public string? Name { get; set; }

    /// <summary>Runner type — same set as single-step jobs (<c>llm-chat</c>, <c>shell</c>, <c>csharp</c>, <c>python</c>, <c>node</c>, <c>file-mutate</c>, <c>file-frontmatter</c>, <c>http</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>List of step ids this step depends on. Empty = root step (executes first).</summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>Step-specific inputs. String values matching the <c>$step_id.field</c> binding syntax are resolved at execution time from upstream outputs.</summary>
    public Dictionary<string, object?> Inputs { get; set; } = new();
}

public sealed class FrontmatterParseException : Exception
{
    public FrontmatterParseException(string message)
        : base(message) { }

    public FrontmatterParseException(string message, Exception inner)
        : base(message, inner) { }
}
