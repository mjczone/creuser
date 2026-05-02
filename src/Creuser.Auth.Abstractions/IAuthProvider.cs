namespace Creuser.Auth.Abstractions;

/// <summary>
/// A pluggable authentication provider. v0.1 ships with the local
/// username+password provider (<c>Creuser.Auth.Providers.Local</c>); Google
/// OAuth lives in <c>Creuser.Auth.Providers.Google</c> as a stub for v0.2+.
/// </summary>
public interface IAuthProvider
{
    /// <summary>Stable identifier — "local", "google", "oidc", etc.</summary>
    string Name { get; }

    /// <summary>
    /// Verify the supplied credentials and return the resolved user, or a
    /// failure indicating why authentication did not succeed.
    /// </summary>
    Task<AuthResult> AuthenticateAsync(AuthCredentials credentials, CancellationToken ct = default);
}

public sealed record AuthCredentials(string Email, string? Password);

public abstract record AuthResult
{
    public sealed record Ok(User User) : AuthResult;

    public sealed record InvalidCredentials() : AuthResult;

    public sealed record Disabled() : AuthResult;

    public sealed record NotSupported(string Reason) : AuthResult;
}
