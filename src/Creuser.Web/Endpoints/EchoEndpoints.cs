using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Contracts.Responses;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public static class EchoEndpoints
{
    public static IEndpointRouteBuilder MapEchoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/echo").WithTags("Diagnostics");

        group
            .MapPost("/", Echo)
            .WithName("Echo")
            .WithSummary("Echo a message back, optionally repeated")
            .WithDescription(
                "Demonstrates the full request/validation/envelope/ProblemDetails chain. Provided as a smoke-test endpoint for the dev proxy and the generated TypeScript client."
            );

        return app;
    }

    private static async Task<Results<Ok<ApiResult<EchoResponse>>, ProblemHttpResult>> Echo(
        EchoRequest request,
        IValidator<EchoRequest> validator
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var errors = validation
                .Errors.GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            return Problems.ValidationFailed(errors);
        }

        var repeat = request.Repeat ?? 1;
        var message = string.Join(' ', Enumerable.Repeat(request.Message, repeat));

        return TypedResults.Ok(new ApiResult<EchoResponse>(new EchoResponse(message)));
    }
}
