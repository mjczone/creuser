using Creuser.Auth.Abstractions;

namespace Creuser.Auth.Providers.Google;

/// <summary>
/// Stub. Lights up in v0.2 alongside SMTP-driven account flows. Reserves
/// the seam so wiring this in later is a configuration change, not a
/// surgical refactor.
/// </summary>
public sealed class GoogleAuthProvider : IAuthProvider
{
    public string Name => "google";

    public Task<AuthResult> AuthenticateAsync(
        AuthCredentials credentials,
        CancellationToken ct = default
    ) =>
        Task.FromResult<AuthResult>(
            new AuthResult.NotSupported(
                "Google OAuth is not configured in this build. Use the local provider, or wait for v0.2."
            )
        );
}
