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
/// Typed representation of frontmatter for v0.1 single-step jobs. The
/// multi-step <c>steps:</c> shape lands when the DAG executor does — until
/// then jobs are <c>type:</c> + body, and the executor wraps the whole
/// thing as a single inline step.
/// </summary>
public sealed class JobScriptFrontmatter
{
    /// <summary>One of <c>llm-chat</c>, <c>shell</c>, <c>csharp</c>, <c>file-mutate</c>, etc. Determines the runner.</summary>
    public string Type { get; set; } = "llm-chat";

    /// <summary>One of <c>deterministic</c>, <c>plan-then-execute</c>, <c>agentic</c>. Defaults to deterministic.</summary>
    public string Pattern { get; set; } = "deterministic";

    /// <summary>Optional runner-specific configuration block. Each runner's input schema defines what's expected here.</summary>
    public Dictionary<string, object?> Inputs { get; set; } = new();

    /// <summary>Free-text declaration of secret filenames the runner may read via SecretsService.</summary>
    public List<string> RequiredSecrets { get; set; } = new();

    /// <summary>Per-job command allow-list for shell-type runners.</summary>
    public List<string> AllowedCommands { get; set; } = new();

    /// <summary>Budgets the executor enforces.</summary>
    public BudgetsBlock? Budgets { get; set; }

    /// <summary>Optional schedule. v0.1 doesn't run a scheduler yet, but we round-trip the value.</summary>
    public ScheduleBlock? Schedule { get; set; }

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

public sealed class FrontmatterParseException : Exception
{
    public FrontmatterParseException(string message)
        : base(message) { }

    public FrontmatterParseException(string message, Exception inner)
        : base(message, inner) { }
}
