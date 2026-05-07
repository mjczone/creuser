using Creuser.Projections.Schema;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

/// <summary>
/// Serves the convention YAML JSON Schema for IDE consumption. Authors add a
/// <c># yaml-language-server: $schema=…</c> line at the top of their convention
/// files and get autocomplete + validation in any modern editor.
///
/// <para>
/// Mirrors the <c>/openapi/v1.json</c> precedent: schema documents live outside
/// the <c>/api/</c> prefix, return raw JSON (no <c>ApiResult</c> envelope), and
/// are unauthenticated so the IDE's yaml-language-server can fetch them
/// directly without a credential roundtrip.
/// </para>
/// </summary>
public static class ConventionsSchemaEndpoints
{
    public static IEndpointRouteBuilder MapConventionsSchemaEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        app.MapGet("/schemas/conventions/v1.json", (Delegate)GetConventionSchema)
            .WithName("GetConventionSchema")
            .WithTags("Schemas")
            .AllowAnonymous();
        return app;
    }

    private static ContentHttpResult GetConventionSchema()
    {
        var schema = ConventionSchemaGenerator.Generate();
        return TypedResults.Text(schema.ToJsonString(), "application/schema+json");
    }
}
