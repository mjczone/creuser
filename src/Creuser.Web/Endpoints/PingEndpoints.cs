using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Responses;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public static class PingEndpoints
{
    public static IEndpointRouteBuilder MapPingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ping").WithTags("Diagnostics");

        group
            .MapGet("/", Ping)
            .WithName("Ping")
            .WithSummary("Liveness probe")
            .WithDescription(
                "Returns a small payload echoing the server's wall-clock time. Used by the SPA and external monitors to confirm the API is reachable through the dev proxy and reverse proxies."
            );

        return app;
    }

    private static Ok<ApiResult<PingResponse>> Ping(TimeProvider time) =>
        TypedResults.Ok(new ApiResult<PingResponse>(new PingResponse("pong", time.GetUtcNow())));
}
