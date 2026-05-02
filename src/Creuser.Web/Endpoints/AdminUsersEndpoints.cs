using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Contracts.Responses;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public static class AdminUsersEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users")
            .WithTags("Admin")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapPost("/", (Delegate)CreateUser).WithName("CreateUser");
        group
            .MapPost("/{id:guid}/reset-password", (Delegate)ResetPassword)
            .WithName("ResetUserPassword");
        group.MapPost("/{id:guid}/active", (Delegate)SetActive).WithName("SetUserActive");
        group.MapGet("/", (Delegate)ListUsers).WithName("ListUsers");

        return app;
    }

    private static async Task<
        Results<Ok<ApiResult<CreateUserResult>>, ProblemHttpResult>
    > CreateUser(
        CreateUserRequest request,
        IValidator<CreateUserRequest> validator,
        IUserStore users,
        IPasswordHasher hasher,
        TimeProvider time
    )
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Problems.ValidationFailed(AuthEndpoints.ToErrorMap(validation));

        if (await users.EmailExistsAsync(request.Email))
            return Problems.EmailAlreadyExists(request.Email);

        var temp = string.IsNullOrEmpty(request.TemporaryPassword)
            ? TempPasswordGenerator.Generate()
            : request.TemporaryPassword;

        var now = time.GetUtcNow().UtcDateTime;
        var user = new User(
            Id: Guid.NewGuid(),
            Email: request.Email,
            DisplayName: request.DisplayName,
            Role: request.Role,
            PasswordHash: hasher.Hash(temp),
            IsActive: true,
            MustChangePassword: true,
            LastLoginAt: null,
            PasswordChangedAt: null,
            CreatedAt: now,
            UpdatedAt: now
        );
        await users.SaveAsync(user);

        return TypedResults.Ok(
            new ApiResult<CreateUserResult>(
                new CreateUserResult(user.Id, user.Email, user.DisplayName, user.Role, temp)
            )
        );
    }

    private static async Task<
        Results<Ok<ApiResult<CreateUserResult>>, ProblemHttpResult>
    > ResetPassword(Guid id, IUserStore users, IPasswordHasher hasher, TimeProvider time)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
            return Problems.UserNotFound(id);

        var temp = TempPasswordGenerator.Generate();
        var now = time.GetUtcNow().UtcDateTime;
        var updated = user with
        {
            PasswordHash = hasher.Hash(temp),
            MustChangePassword = true,
            PasswordChangedAt = now,
            UpdatedAt = now,
        };
        await users.SaveAsync(updated);

        return TypedResults.Ok(
            new ApiResult<CreateUserResult>(
                new CreateUserResult(
                    updated.Id,
                    updated.Email,
                    updated.DisplayName,
                    updated.Role,
                    temp
                )
            )
        );
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> SetActive(
        Guid id,
        SetActiveRequest body,
        IUserStore users,
        TimeProvider time
    )
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
            return Problems.UserNotFound(id);

        var updated = user with
        {
            IsActive = body.IsActive,
            UpdatedAt = time.GetUtcNow().UtcDateTime,
        };
        await users.SaveAsync(updated);
        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    private static async Task<Ok<ApiResult<IReadOnlyList<UserResult>>>> ListUsers(
        IUserStore users,
        int? skip,
        int? take
    )
    {
        var rows = await users.ListAsync(Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 200));
        IReadOnlyList<UserResult> result = rows.Select(AuthEndpoints.ToResult).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<UserResult>>(result));
    }

    public sealed record SetActiveRequest(bool IsActive);
}
