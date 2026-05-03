using System.Text.Json;

namespace Creuser.Scripting;

/// <summary>
/// Step inputs reach the executor in three shapes:
/// <list type="bullet">
///   <item>YAML-derived from frontmatter — nested mappings come back as <c>Dictionary&lt;object, object&gt;</c> with object keys; sequences as <c>List&lt;object&gt;</c>.</item>
///   <item>JSON-derived from the per-run <c>parameters</c> body — System.Text.Json materializes nested values as <see cref="JsonElement"/>.</item>
///   <item>Already-canonical — <c>Dictionary&lt;string, object?&gt;</c> / <c>List&lt;object?&gt;</c> from in-process callers.</item>
/// </list>
///
/// Runners shouldn't have to hand-walk three different graph shapes. This
/// normalizer rewrites the whole tree into the canonical form:
/// <c>Dictionary&lt;string, object?&gt;</c> for objects, <c>List&lt;object?&gt;</c>
/// for arrays, and CLR primitives (string / long / double / bool / null) for
/// scalars. Runners then read inputs with a single set of casts.
///
/// <para>
/// The cost is one walk per step at executor entry — negligible for the
/// shapes we deal with (sub-millisecond on dictionaries with hundreds of
/// keys), and the simplification is worth it.
/// </para>
/// </summary>
internal static class InputsNormalizer
{
    public static IReadOnlyDictionary<string, object?> NormalizeRoot(
        IReadOnlyDictionary<string, object?> input
    )
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in input)
            result[kv.Key] = Normalize(kv.Value);
        return result;
    }

    public static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement el:
                return NormalizeJsonElement(el);
            // String must come before IEnumerable — strings are
            // IEnumerable<char> but should be treated as scalars.
            case string s:
                return s;
            // Non-generic IDictionary covers both Dictionary<object,object>
            // (YAML's mapping shape) and Dictionary<string,object?> at the
            // CLR level. Generics nullability is erased at runtime, so we
            // can't dispatch on `IDictionary<object,object?>` vs
            // `IDictionary<object,object>` as separate cases.
            case System.Collections.IDictionary dict:
                return NormalizeDictionary(dict);
            case System.Collections.IEnumerable seq:
                return NormalizeSequence(seq);
            default:
                return value;
        }
    }

    private static object? NormalizeJsonElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var i))
                    return i;
                if (el.TryGetDouble(out var d))
                    return d;
                return el.GetRawText();
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray())
                    list.Add(NormalizeJsonElement(item));
                return list;
            }
            case JsonValueKind.Object:
            {
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in el.EnumerateObject())
                    obj[p.Name] = NormalizeJsonElement(p.Value);
                return obj;
            }
            default:
                return el.GetRawText();
        }
    }

    private static Dictionary<string, object?> NormalizeDictionary(System.Collections.IDictionary d)
    {
        // foreach over a non-generic IDictionary uses IDictionaryEnumerator
        // which yields DictionaryEntry — avoid `Cast<DictionaryEntry>()`
        // because it forces IEnumerable<T> path that some generic
        // dictionaries override to yield KeyValuePair instead.
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        var e = d.GetEnumerator();
        try
        {
            while (e.MoveNext())
            {
                var entry = (System.Collections.DictionaryEntry)e.Current;
                r[entry.Key.ToString() ?? string.Empty] = Normalize(entry.Value);
            }
        }
        finally
        {
            (e as IDisposable)?.Dispose();
        }
        return r;
    }

    private static List<object?> NormalizeSequence(System.Collections.IEnumerable seq)
    {
        var list = new List<object?>();
        foreach (var item in seq)
            list.Add(Normalize(item));
        return list;
    }
}
