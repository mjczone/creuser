using System.Text.RegularExpressions;

namespace Creuser.Scripting;

/// <summary>
/// Resolves step input bindings for multi-step DAGs. The binding syntax is
/// <c>$&lt;namespace&gt;.&lt;path&gt;</c> where:
/// <list type="bullet">
///   <item><c>$step_id.field</c> — the named output of an upstream step.</item>
///   <item><c>$step_id.field.sub</c> — nested dict navigation.</item>
///   <item><c>$step_id.array[0]</c> — indexed array element.</item>
///   <item><c>$params.name</c> — a per-run parameter.</item>
///   <item><c>$step_id</c> — the entire output dict (no dot path).</item>
/// </list>
///
/// <para>
/// Bindings are <em>whole-value</em> in v0.1: an input value that is a
/// string starting with <c>$</c> is replaced with the bound value (which
/// can be any shape — string, number, object, array). Mid-string
/// interpolation (<c>"hello $step.name"</c>) is not supported in v0.1 —
/// operators who need composition use a <c>python</c> or <c>node</c> step
/// with the binding resolved into the script's input.
/// </para>
///
/// <para>
/// Resolution failures (unknown step id, missing field, out-of-range
/// index, type mismatch) raise <see cref="StepBindingException"/>. The
/// executor catches and surfaces as a clean step failure with the
/// referencing step in scope.
/// </para>
/// </summary>
internal static class StepBindingResolver
{
    private static readonly Regex BindingPattern = new(
        @"^\$([a-zA-Z_][a-zA-Z0-9_-]*)((?:\.[a-zA-Z_][a-zA-Z0-9_-]*|\[\d+\])*)$",
        RegexOptions.Compiled
    );

    public static IReadOnlyDictionary<string, object?> Resolve(
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> stepOutputs,
        IReadOnlyDictionary<string, object?> parameters
    )
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in inputs)
            result[kv.Key] = ResolveValue(kv.Value, stepOutputs, parameters);
        return result;
    }

    private static object? ResolveValue(
        object? value,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> stepOutputs,
        IReadOnlyDictionary<string, object?> parameters
    )
    {
        switch (value)
        {
            case null:
                return null;
            case string s when s.StartsWith('$'):
                return ResolveBinding(s, stepOutputs, parameters);
            case string s:
                return s;
            case IReadOnlyDictionary<string, object?> dict:
            {
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kv in dict)
                    copy[kv.Key] = ResolveValue(kv.Value, stepOutputs, parameters);
                return copy;
            }
            case IList<object?> list:
            {
                var copy = new List<object?>(list.Count);
                foreach (var item in list)
                    copy.Add(ResolveValue(item, stepOutputs, parameters));
                return copy;
            }
            default:
                return value;
        }
    }

    private static object? ResolveBinding(
        string token,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> stepOutputs,
        IReadOnlyDictionary<string, object?> parameters
    )
    {
        var match = BindingPattern.Match(token);
        if (!match.Success)
        {
            // Looks like a binding (starts with $) but isn't a valid one —
            // surface the error rather than silently leaving the literal
            // through. This catches typos like `$ step.field`.
            throw new StepBindingException(
                $"Invalid binding syntax: '{token}'. Expected `$step_id.field`, `$step_id.field.sub`, `$step_id.array[0]`, or `$params.name`."
            );
        }

        var ns = match.Groups[1].Value;
        var path = match.Groups[2].Value;

        // Pick the namespace root.
        object? root;
        if (string.Equals(ns, "params", StringComparison.Ordinal))
        {
            root = parameters;
        }
        else if (stepOutputs.TryGetValue(ns, out var stepOut))
        {
            root = stepOut;
        }
        else
        {
            throw new StepBindingException(
                $"Binding '{token}' references unknown step or namespace '{ns}'. Available: {string.Join(", ", stepOutputs.Keys.Concat(new[] { "params" }))}."
            );
        }

        if (path.Length == 0)
            return root;

        // Walk the path segments. A segment is either `.name` (dict key) or
        // `[index]` (list index).
        var current = root;
        var segments = SplitPath(path);
        foreach (var seg in segments)
        {
            if (current is null)
                throw new StepBindingException(
                    $"Binding '{token}' navigated through null at segment '{seg}'."
                );
            if (seg.IsIndex)
            {
                if (current is not IList<object?> list)
                    throw new StepBindingException(
                        $"Binding '{token}' segment '[{seg.Index}]' expected an array; got {current.GetType().Name}."
                    );
                if (seg.Index < 0 || seg.Index >= list.Count)
                    throw new StepBindingException(
                        $"Binding '{token}' segment '[{seg.Index}]' out of range (array length {list.Count})."
                    );
                current = list[seg.Index];
            }
            else
            {
                if (current is not IReadOnlyDictionary<string, object?> dict)
                    throw new StepBindingException(
                        $"Binding '{token}' segment '.{seg.Name}' expected an object; got {current.GetType().Name}."
                    );
                if (!dict.TryGetValue(seg.Name, out var next))
                    throw new StepBindingException(
                        $"Binding '{token}' segment '.{seg.Name}' not found. Available keys: {string.Join(", ", dict.Keys)}."
                    );
                current = next;
            }
        }
        return current;
    }

    private static IEnumerable<PathSegment> SplitPath(string path)
    {
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var start = i + 1;
                var end = start;
                while (end < path.Length && path[end] != '.' && path[end] != '[')
                    end++;
                yield return new PathSegment(false, path[start..end], 0);
                i = end;
            }
            else if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                    throw new StepBindingException($"Unterminated index in binding path '{path}'.");
                var indexText = path[(i + 1)..close];
                if (!int.TryParse(indexText, out var idx))
                    throw new StepBindingException(
                        $"Index '{indexText}' is not an integer in binding path '{path}'."
                    );
                yield return new PathSegment(true, "", idx);
                i = close + 1;
            }
            else
            {
                throw new StepBindingException(
                    $"Unexpected character '{path[i]}' in binding path '{path}'."
                );
            }
        }
    }

    private readonly record struct PathSegment(bool IsIndex, string Name, int Index);
}

public sealed class StepBindingException : Exception
{
    public StepBindingException(string message)
        : base(message) { }
}
