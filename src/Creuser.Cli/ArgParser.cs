namespace Creuser.Cli;

/// <summary>
/// Minimal command-line argument parser. Walks <c>argv</c> once and produces
/// a positional list + an options dictionary keyed by long flag name
/// (without the leading <c>--</c>). Booleans can use <c>--flag</c> or
/// <c>--flag=true</c>; valued options use <c>--key value</c> or
/// <c>--key=value</c>. We avoid System.CommandLine to keep the dependency
/// graph small and the binary slim.
/// </summary>
public sealed class ParsedArgs
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;

    public string GetRequired(string key, string commandName)
    {
        if (!Options.TryGetValue(key, out var v) || string.IsNullOrEmpty(v))
            throw new CliUserError($"{commandName}: missing required option --{key}.");
        return v;
    }

    public int? GetInt(string key)
    {
        var raw = Get(key);
        if (raw is null)
            return null;
        if (!int.TryParse(raw, out var n))
            throw new CliUserError($"--{key} must be an integer; got '{raw}'.");
        return n;
    }

    public bool HasFlag(string key) => Flags.Contains(key);

    public static ParsedArgs Parse(string[] args)
    {
        var p = new ParsedArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    p.Options[name[..eq]] = name[(eq + 1)..];
                    continue;
                }
                // Look ahead — if the next arg is a value (not a flag), consume it;
                // otherwise treat as a boolean flag.
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    p.Options[name] = args[++i];
                }
                else
                {
                    p.Flags.Add(name);
                }
                continue;
            }
            p.Positional.Add(a);
        }
        return p;
    }
}

/// <summary>
/// Exception that maps to a clean user-facing error message + non-zero exit.
/// Distinguishes recoverable user mistakes from internal bugs.
/// </summary>
public sealed class CliUserError : Exception
{
    public CliUserError(string message)
        : base(message) { }
}
