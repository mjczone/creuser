namespace Creuser.Auth.Abstractions;

/// <summary>
/// Domain user record. PascalCase by convention — this is the type passed
/// around the application and exposed as the auth contract. Persistence
/// entities (lowercase property names matching DB columns) live in
/// <c>Creuser.Persistence.Tables</c>.
/// </summary>
public sealed record User(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    string PasswordHash,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    DateTime? PasswordChangedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
}
