using Creuser.Scripting;

namespace Creuser.Scripting.Tests;

public class StepBindingResolverTests
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, object?>> NoOutputs =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, object?> NoParams = new(StringComparer.Ordinal);

    [Fact]
    public void Resolve_NonStringValues_PassThrough()
    {
        var inputs = new Dictionary<string, object?>
        {
            ["count"] = 42,
            ["enabled"] = true,
            ["name"] = "literal",
        };
        var result = StepBindingResolver.Resolve(inputs, NoOutputs, NoParams);
        Assert.Equal(42, result["count"]);
        Assert.Equal(true, result["enabled"]);
        Assert.Equal("literal", result["name"]);
    }

    [Fact]
    public void Resolve_WholeStepReference_ReturnsEntireOutputDict()
    {
        var fetchOutputs = new Dictionary<string, object?> { ["status"] = 200, ["body"] = "hello" };
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["fetch"] = fetchOutputs,
        };

        var result = StepBindingResolver.Resolve(
            new Dictionary<string, object?> { ["upstream"] = "$fetch" },
            stepOutputs,
            NoParams
        );

        var bound = (IReadOnlyDictionary<string, object?>)result["upstream"]!;
        Assert.Equal(200, bound["status"]);
        Assert.Equal("hello", bound["body"]);
    }

    [Fact]
    public void Resolve_FieldPath_ReturnsScalar()
    {
        var fetchOutputs = new Dictionary<string, object?> { ["status"] = 200, ["body"] = "hello" };
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["fetch"] = fetchOutputs,
        };

        var result = StepBindingResolver.Resolve(
            new Dictionary<string, object?> { ["s"] = "$fetch.status" },
            stepOutputs,
            NoParams
        );

        Assert.Equal(200, result["s"]);
    }

    [Fact]
    public void Resolve_NestedDictPath_NavigatesDeep()
    {
        var fetchOutputs = new Dictionary<string, object?>
        {
            ["payload"] = new Dictionary<string, object?>
            {
                ["nested"] = new Dictionary<string, object?> { ["leaf"] = "value" },
            },
        };
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["fetch"] = fetchOutputs,
        };

        var result = StepBindingResolver.Resolve(
            new Dictionary<string, object?> { ["v"] = "$fetch.payload.nested.leaf" },
            stepOutputs,
            NoParams
        );

        Assert.Equal("value", result["v"]);
    }

    [Fact]
    public void Resolve_ArrayIndex_NavigatesElement()
    {
        var fetchOutputs = new Dictionary<string, object?>
        {
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["title"] = "first" },
                new Dictionary<string, object?> { ["title"] = "second" },
            },
        };
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["fetch"] = fetchOutputs,
        };

        var result = StepBindingResolver.Resolve(
            new Dictionary<string, object?> { ["t"] = "$fetch.items[1].title" },
            stepOutputs,
            NoParams
        );

        Assert.Equal("second", result["t"]);
    }

    [Fact]
    public void Resolve_ParamsNamespace_LooksUpInParameters()
    {
        var parameters = new Dictionary<string, object?> { ["topic"] = "machine learning" };
        var result = StepBindingResolver.Resolve(
            new Dictionary<string, object?> { ["q"] = "$params.topic" },
            NoOutputs,
            parameters
        );
        Assert.Equal("machine learning", result["q"]);
    }

    [Fact]
    public void Resolve_UnknownStep_Throws()
    {
        var ex = Assert.Throws<StepBindingException>(() =>
            StepBindingResolver.Resolve(
                new Dictionary<string, object?> { ["x"] = "$ghost.field" },
                NoOutputs,
                NoParams
            )
        );
        Assert.Contains("unknown step or namespace 'ghost'", ex.Message);
    }

    [Fact]
    public void Resolve_MissingField_Throws()
    {
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["fetch"] = new Dictionary<string, object?> { ["status"] = 200 },
        };
        var ex = Assert.Throws<StepBindingException>(() =>
            StepBindingResolver.Resolve(
                new Dictionary<string, object?> { ["x"] = "$fetch.missing" },
                stepOutputs,
                NoParams
            )
        );
        Assert.Contains(".missing' not found", ex.Message);
    }

    [Fact]
    public void Resolve_IndexOutOfRange_Throws()
    {
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["fetch"] = new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "only" },
            },
        };
        var ex = Assert.Throws<StepBindingException>(() =>
            StepBindingResolver.Resolve(
                new Dictionary<string, object?> { ["x"] = "$fetch.items[5]" },
                stepOutputs,
                NoParams
            )
        );
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void Resolve_TypeMismatch_Throws()
    {
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["fetch"] = new Dictionary<string, object?> { ["status"] = 200 },
        };
        var ex = Assert.Throws<StepBindingException>(() =>
            StepBindingResolver.Resolve(
                new Dictionary<string, object?> { ["x"] = "$fetch.status[0]" },
                stepOutputs,
                NoParams
            )
        );
        Assert.Contains("expected an array", ex.Message);
    }

    [Fact]
    public void Resolve_MalformedSyntax_Throws()
    {
        var ex = Assert.Throws<StepBindingException>(() =>
            StepBindingResolver.Resolve(
                new Dictionary<string, object?> { ["x"] = "$bad..thing" },
                NoOutputs,
                NoParams
            )
        );
        Assert.Contains("Invalid binding syntax", ex.Message);
    }

    [Fact]
    public void Resolve_NestedDictsRecurseIntoBindings()
    {
        // Bindings inside nested dicts and lists are also resolved.
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["a"] = new Dictionary<string, object?> { ["k"] = "value-from-a" },
        };
        var inputs = new Dictionary<string, object?>
        {
            ["wrapper"] = new Dictionary<string, object?> { ["nested"] = "$a.k" },
            ["list"] = new List<object?> { "$a.k", "literal" },
        };

        var result = StepBindingResolver.Resolve(inputs, stepOutputs, NoParams);
        var wrapper = (IReadOnlyDictionary<string, object?>)result["wrapper"]!;
        Assert.Equal("value-from-a", wrapper["nested"]);
        var list = (IList<object?>)result["list"]!;
        Assert.Equal("value-from-a", list[0]);
        Assert.Equal("literal", list[1]);
    }

    [Fact]
    public void Resolve_LiteralStringStartingWithDollar_ButValidPattern_StillTreatedAsBinding()
    {
        // Edge case: a literal string starting with `$` is treated as a
        // binding attempt. Operators who genuinely need to start a literal
        // value with `$` can prefix with another character or pre-compose
        // in a python step. This is the v0.1 contract — keeps the resolver
        // simple at the cost of one footgun.
        var ex = Assert.Throws<StepBindingException>(() =>
            StepBindingResolver.Resolve(
                new Dictionary<string, object?> { ["x"] = "$5.99" },
                NoOutputs,
                NoParams
            )
        );
        // "5" isn't a valid identifier so we get the syntax error rather
        // than the unknown-step error.
        Assert.Contains("Invalid binding syntax", ex.Message);
    }
}
