using Creuser.Auth.Abstractions;

namespace Creuser.Auth.Providers.Local;

/// <summary>
/// Local username + password provider. Verifies credentials against the
/// configured <see cref="IUserStore"/> using <see cref="IPasswordHasher"/>.
/// </summary>
public sealed class LocalAuthProvider : IAuthProvider
{
    private readonly IUserStore _users;
    private readonly IPasswordHasher _hasher;

    public LocalAuthProvider(IUserStore users, IPasswordHasher hasher)
    {
        _users = users;
        _hasher = hasher;
    }

    public string Name => "local";

    public async Task<AuthResult> AuthenticateAsync(
        AuthCredentials credentials,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(credentials.Password))
            return new AuthResult.InvalidCredentials();

        var user = await _users.FindByEmailAsync(credentials.Email, ct);
        if (user is null || !_hasher.Verify(credentials.Password, user.PasswordHash))
            return new AuthResult.InvalidCredentials();
        if (!user.IsActive)
            return new AuthResult.Disabled();

        return new AuthResult.Ok(user);
    }
}
