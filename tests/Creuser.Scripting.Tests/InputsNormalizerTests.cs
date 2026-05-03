using System.Text.Json;
using Creuser.Scripting;

namespace Creuser.Scripting.Tests;

public class InputsNormalizerTests
{
    [Fact]
    public void Normalize_Null_ReturnsNull()
    {
        Assert.Null(InputsNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_String_ReturnsStringNotIEnumerable()
    {
        // Strings are IEnumerable<char>; the normalizer must treat them as
        // scalars or every input would explode into char arrays.
        var result = InputsNormalizer.Normalize("hello");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Normalize_Primitives_PassThroughUnchanged()
    {
        Assert.Equal(42, InputsNormalizer.Normalize(42));
        Assert.Equal(3.14, InputsNormalizer.Normalize(3.14));
        Assert.Equal(true, InputsNormalizer.Normalize(true));
    }

    [Fact]
    public void Normalize_YamlObjectKeyedDict_ReturnsStringKeyedDict()
    {
        // Simulate YamlDotNet's nested mapping shape: Dictionary<object, object>.
        var yamlLike = new Dictionary<object, object?> { ["op"] = "create", ["path"] = "foo.md" };

        var result = (Dictionary<string, object?>)InputsNormalizer.Normalize(yamlLike)!;

        Assert.Equal("create", result["op"]);
        Assert.Equal("foo.md", result["path"]);
    }

    [Fact]
    public void Normalize_NestedYamlMapping_RecursesDeep()
    {
        var nested = new Dictionary<object, object?>
        {
            ["outer"] = new Dictionary<object, object?>
            {
                ["inner"] = new Dictionary<object, object?> { ["leaf"] = 42 },
            },
        };

        var result = (Dictionary<string, object?>)InputsNormalizer.Normalize(nested)!;
        var outer = (Dictionary<string, object?>)result["outer"]!;
        var inner = (Dictionary<string, object?>)outer["inner"]!;
        Assert.Equal(42, inner["leaf"]);
    }

    [Fact]
    public void Normalize_YamlSequence_BecomesListOfNormalized()
    {
        var seq = new List<object?>
        {
            "git",
            new Dictionary<object, object?> { ["op"] = "create" },
        };

        var result = (List<object?>)InputsNormalizer.Normalize(seq)!;
        Assert.Equal(2, result.Count);
        Assert.Equal("git", result[0]);
        var op = (Dictionary<string, object?>)result[1]!;
        Assert.Equal("create", op["op"]);
    }

    [Fact]
    public void Normalize_JsonElementObject_BecomesStringKeyedDict()
    {
        var doc = JsonDocument.Parse("""{"op": "create", "path": "foo.md", "size": 42}""");
        var result = (Dictionary<string, object?>)InputsNormalizer.Normalize(doc.RootElement)!;

        Assert.Equal("create", result["op"]);
        Assert.Equal("foo.md", result["path"]);
        Assert.Equal(42L, result["size"]);
    }

    [Fact]
    public void Normalize_JsonElementArray_BecomesListOfNormalized()
    {
        var doc = JsonDocument.Parse("""["a", 1, true, null]""");
        var result = (List<object?>)InputsNormalizer.Normalize(doc.RootElement)!;
        Assert.Equal(4, result.Count);
        Assert.Equal("a", result[0]);
        Assert.Equal(1L, result[1]);
        Assert.Equal(true, result[2]);
        Assert.Null(result[3]);
    }

    [Fact]
    public void Normalize_JsonElementNumberPrefersInt64WhenWhole()
    {
        var doc = JsonDocument.Parse("""{"i": 42, "d": 3.14}""");
        var result = (Dictionary<string, object?>)InputsNormalizer.Normalize(doc.RootElement)!;
        Assert.Equal(42L, result["i"]);
        Assert.Equal(3.14, result["d"]);
    }

    [Fact]
    public void NormalizeRoot_PreservesTopLevelKeys()
    {
        var input = new Dictionary<string, object?>
        {
            ["a"] = 1,
            ["b"] = new Dictionary<object, object?> { ["nested"] = "value" },
        };

        var result = InputsNormalizer.NormalizeRoot(input);
        Assert.Equal(1, result["a"]);
        var b = (Dictionary<string, object?>)result["b"]!;
        Assert.Equal("value", b["nested"]);
    }
}
