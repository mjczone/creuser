namespace Creuser.Web.Contracts.Requests;

/// <summary>
/// Admin invite request. <see cref="TemporaryPassword"/> is optional —
/// if omitted, the server generates a strong default and returns it in
/// the response so the admin can pass it on out-of-band.
/// </summary>
public sealed record CreateUserRequest(
    string Email,
    string DisplayName,
    string Role,
    string? TemporaryPassword
);
