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

    public static ProblemHttpResult LastAdmin(string detail) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "last-admin",
                Title = "Last remaining admin",
                Status = StatusCodes.Status409Conflict,
                Detail = detail,
            }
        );

    public static ProblemHttpResult SelfActionNotAllowed(string detail) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "self-action-not-allowed",
                Title = "You cannot perform this action on your own account",
                Status = StatusCodes.Status409Conflict,
                Detail = detail,
            }
        );

    public static ProblemHttpResult WorkspaceNotFound(string identifier) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "workspace-not-found",
                Title = "Workspace not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"No workspace exists matching '{identifier}'.",
            }
        );

    public static ProblemHttpResult WorkspaceTypeNotSupported(
        string identifier,
        string type,
        string operation
    ) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "workspace-type-not-supported",
                Title = "Operation not supported for this workspace type",
                Status = StatusCodes.Status400BadRequest,
                Detail =
                    $"Workspace '{identifier}' is of type '{type}'; the '{operation}' operation is only supported for git workspaces.",
            }
        );

    public static ProblemHttpResult SlugAlreadyExists(string slug) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "slug-already-exists",
                Title = "Slug is already in use",
                Status = StatusCodes.Status409Conflict,
                Detail = $"A workspace with slug '{slug}' already exists.",
            }
        );

    public static ProblemHttpResult JobScriptNotFound(string identifier) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "job-script-not-found",
                Title = "Job script not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"No job script exists matching '{identifier}'.",
            }
        );

    public static ProblemHttpResult JobScriptSlugAlreadyExists(string slug) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "job-script-slug-already-exists",
                Title = "Job script slug is already in use",
                Status = StatusCodes.Status409Conflict,
                Detail = $"A job script with slug '{slug}' already exists in this workspace.",
            }
        );

    public static ProblemHttpResult JobRunNotFound(Guid id) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "job-run-not-found",
                Title = "Job run not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"No job run exists with id '{id}'.",
            }
        );

    public static ProblemHttpResult ScheduleNotFound(Guid id) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Type = TypeBase + "schedule-not-found",
                Title = "Schedule not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"No schedule exists with id '{id}'.",
            }
        );
}
