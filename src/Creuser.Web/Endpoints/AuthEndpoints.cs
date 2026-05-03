using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Contracts.Responses;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", (Delegate)Login).WithName("Login").AllowAnonymous();
        group.MapPost("/logout", (Delegate)Logout).WithName("Logout");
        group.MapGet("/me", (Delegate)Me).WithName("GetCurrentUser");
        group.MapPost("/change-password", (Delegate)ChangePassword).WithName("ChangePassword");

        return app;
    }

    private static async Task<Results<Ok<ApiResult<UserResult>>, ProblemHttpResult>> Login(
        LoginRequest request,
        IValidator<LoginRequest> validator,
        IAuthProvider provider,
        IUserStore users,
        TimeProvider time,
        HttpContext http
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Problems.ValidationFailed(ToErrorMap(validation));

        var result = await provider.AuthenticateAsync(
            new AuthCredentials(request.Email, request.Password)
        );

        switch (result)
        {
            case AuthResult.Ok ok:
                await CookieAuthHelpers.SignInAsync(http, ok.User);
                await users.UpdateLastLoginAsync(ok.User.Id, time.GetUtcNow().UtcDateTime);
                return TypedResults.Ok(new ApiResult<UserResult>(ToResult(ok.User)));
            case AuthResult.Disabled:
                return Problems.Forbidden("This account is disabled.");
            default:
                return Problems.InvalidCredentials();
        }
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> Logout(
        HttpContext http
    )
    {
        await http.SignOutAsync(CookieAuthHelpers.SchemeName);
        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    private static async Task<Results<Ok<ApiResult<UserResult>>, ProblemHttpResult>> Me(
        HttpContext http,
        IUserStore users
    )
    {
        var id = CookieAuthHelpers.GetUserId(http);
        if (id is null)
            return Problems.Unauthorized();
        var user = await users.FindByIdAsync(id.Value);
        if (user is null)
        {
            await http.SignOutAsync(CookieAuthHelpers.SchemeName);
            return Problems.Unauthorized("Session no longer valid.");
        }
        return TypedResults.Ok(new ApiResult<UserResult>(ToResult(user)));
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> ChangePassword(
        ChangePasswordRequest request,
        IValidator<ChangePasswordRequest> validator,
        IUserStore users,
        IPasswordHasher hasher,
        TimeProvider time,
        HttpContext http
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Problems.ValidationFailed(ToErrorMap(validation));

        var id = CookieAuthHelpers.GetUserId(http);
        if (id is null)
            return Problems.Unauthorized();
        var user = await users.FindByIdAsync(id.Value);
        if (user is null)
            return Problems.Unauthorized("Session no longer valid.");

        if (!hasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Problems.InvalidCredentials();

        var now = time.GetUtcNow().UtcDateTime;
        var updated = user with
        {
            PasswordHash = hasher.Hash(request.NewPassword),
            MustChangePassword = false,
            PasswordChangedAt = now,
            UpdatedAt = now,
        };
        await users.SaveAsync(updated);

        // Refresh the cookie so its claims reflect the cleared MustChangePassword flag.
        await CookieAuthHelpers.SignInAsync(http, updated);

        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    internal static UserResult ToResult(User u) =>
        new(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.MustChangePassword, u.LastLoginAt);

    internal static IDictionary<string, string[]> ToErrorMap(
        FluentValidation.Results.ValidationResult result
    ) =>
        result
            .Errors.GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
}
