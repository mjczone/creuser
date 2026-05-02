namespace Creuser.Web.Contracts.Responses;

public sealed record UserResult(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    bool MustChangePassword
);
