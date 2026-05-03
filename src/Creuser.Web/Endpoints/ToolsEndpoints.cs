using Creuser.Auth.Abstractions;
using Creuser.Core.Execution;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public static class ToolsEndpoints
{
    public static IEndpointRouteBuilder MapToolsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tools")
            .WithTags("Tools")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", (Delegate)List).WithName("ListTools");

        return app;
    }

    [AiCapability(
        "tools.list",
        "tools",
        "Available tools",
        "Browse the binaries the platform's shell + script runners are aware of — the curated palette baked into the deployment image plus any plugin-contributed tools. Used by the Jobs editor to populate the `allowed_commands` picker.",
        "list tools",
        "what binaries",
        "available tools",
        "what is installed",
        "what commands are available",
        Route = "/w/:slug/settings/jobs",
        RequiresRole = Roles.Admin
    )]
    private static Ok<ApiResult<IReadOnlyList<ToolEntry>>> List(IToolCatalog catalog)
    {
        return TypedResults.Ok(new ApiResult<IReadOnlyList<ToolEntry>>(catalog.List()));
    }
}
