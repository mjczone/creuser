using System.Text.Json.Nodes;
using Creuser.Projections.Schema;

namespace Creuser.Projections.Tests;

public class ConventionSchemaGeneratorTests
{
    [Fact]
    public void Generate_TopLevelShape_HasIdAndMatchRequired()
    {
        var schema = ConventionSchemaGenerator.Generate();
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        var required = (JsonArray)schema["required"]!;
        Assert.Contains("id", required.Select(n => n!.GetValue<string>()));
        Assert.Contains("match", required.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Generate_Relationship_HasDisplayFieldsAndLegacyResolution()
    {
        var schema = ConventionSchemaGenerator.Generate();
        var relItem = schema["properties"]!["relationships"]!["items"]!.AsObject();
        var relProps = relItem["properties"]!.AsObject();

        Assert.Contains("kind", relProps);
        Assert.Contains("name", relProps);
        Assert.Contains("icon", relProps);
        Assert.Contains("description", relProps);
        Assert.Contains("order", relProps);
        Assert.Contains("select_path", relProps);
        Assert.Contains("select_frontmatter", relProps);
        Assert.Contains("target_kind", relProps);
        Assert.Contains("inverse", relProps);
        Assert.Contains("inverse_name", relProps);
        Assert.Contains("inverse_icon", relProps);
    }

    [Fact]
    public void Generate_ComputedAccessors_EnumerateRegistry()
    {
        var schema = ConventionSchemaGenerator.Generate();
        var computedValues = schema["properties"]!["metadata"]!["properties"]!["computed"]![
            "additionalProperties"
        ]!.AsObject();
        var enumValues = ((JsonArray)computedValues["enum"]!)
            .Select(n => n!.GetValue<string>())
            .ToList();

        Assert.Contains("file.line_count", enumValues);
        Assert.Contains("file.size", enumValues);
        Assert.Contains("file.mtime", enumValues);
        Assert.Contains("file.extension", enumValues);
        Assert.Contains("path.stem", enumValues);
        Assert.Contains("path.parent_dir", enumValues);
        Assert.Contains("body.title", enumValues);
        Assert.Contains("body.word_count", enumValues);
    }

    [Fact]
    public void Generate_TargetKind_IncludesAnyAndWorkspaceKinds()
    {
        var schema = ConventionSchemaGenerator.Generate(
            workspaceKinds: new[] { "adr", "plan", "checklist" }
        );
        // target_kind now accepts both a string (single kind / "any") and an
        // array (multi-kind whitelist) — wrapped in oneOf. Probe the string variant's enum.
        var stringForm = (
            (JsonArray)
                schema["properties"]!["relationships"]!["items"]!["properties"]!["target_kind"]![
                    "oneOf"
                ]!
        )[0]!;
        var values = ((JsonArray)stringForm["enum"]!).Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("any", values);
        Assert.Contains("adr", values);
        Assert.Contains("plan", values);
        Assert.Contains("checklist", values);
    }

    [Fact]
    public void Generate_Relationship_HasNewResolutionShape()
    {
        var schema = ConventionSchemaGenerator.Generate();
        var relProps = schema["properties"]!["relationships"]!["items"]!["properties"]!.AsObject();

        Assert.Contains("source", relProps);
        Assert.Contains("filter", relProps);
        Assert.Contains("interpret", relProps);
        Assert.Contains("metadata", relProps);

        var interpret = ((JsonArray)relProps["interpret"]!["enum"]!)
            .Select(n => n!.GetValue<string>())
            .ToList();
        Assert.Contains("auto", interpret);
        Assert.Contains("path", interpret);
        Assert.Contains("glob", interpret);
        Assert.Contains("url", interpret);
        Assert.Contains("slug", interpret);
    }

    [Fact]
    public void Generate_HasSchemaIdAndDraft()
    {
        var schema = ConventionSchemaGenerator.Generate();
        Assert.Equal(
            "https://json-schema.org/draft/2020-12/schema",
            schema["$schema"]!.GetValue<string>()
        );
        Assert.Contains("conventions/schema/v1.json", schema["$id"]!.GetValue<string>());
    }
}
