namespace Creuser.Web.Contracts.Responses;

/// <summary>
/// One-time payload returned to the admin after creating a user. The
/// <see cref="TemporaryPassword"/> is the value the admin must convey to
/// the new user out-of-band (Slack/text). It will not be retrievable again.
/// </summary>
public sealed record CreateUserResult(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string TemporaryPassword
);
