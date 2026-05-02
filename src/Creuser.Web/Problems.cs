using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Creuser.Web;

public static class Problems
{
    private const string TypeBase = "https://creuser.dev/problems/";

    public static ProblemHttpResult ValidationFailed(IDictionary<string, string[]> errors) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "validation-failed",
                Title = "One or more validation errors occurred",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["errors"] = errors },
            }
        );

    public static ProblemHttpResult Unauthorized(string detail = "Authentication required.") =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "unauthorized",
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized,
                Detail = detail,
            }
        );

    public static ProblemHttpResult Forbidden(string detail = "You do not have permission.") =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "forbidden",
                Title = "Forbidden",
                Status = StatusCodes.Status403Forbidden,
                Detail = detail,
            }
        );

    public static ProblemHttpResult EmailAlreadyExists(string email) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "email-already-exists",
                Title = "Email already in use",
                Status = StatusCodes.Status409Conflict,
                Detail = $"A user with email '{email}' already exists.",
            }
        );

    public static ProblemHttpResult UserNotFound(Guid id) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "user-not-found",
                Title = "User not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"No user exists with ID '{id}'.",
            }
        );

    public static ProblemHttpResult InvalidCredentials() =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "invalid-credentials",
                Title = "Invalid email or password",
                Status = StatusCodes.Status401Unauthorized,
            }
        );

    public static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "not-found",
                Title = "Resource not found",
                Status = StatusCodes.Status404NotFound,
                Detail = detail,
            }
        );

    public static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "conflict",
                Title = "Conflict",
                Status = StatusCodes.Status409Conflict,
                Detail = detail,
            }
        );

    public static ProblemHttpResult InternalError(string detail) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "internal-error",
                Title = "Unexpected server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = detail,
            }
        );
}
