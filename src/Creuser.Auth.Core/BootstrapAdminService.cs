using Creuser.Auth.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Creuser.Auth.Core;

/// <summary>
/// Idempotently seeds an initial admin user on the very first boot, when
/// no users exist in the store yet. Reads:
///   CREUSER_BOOTSTRAP_EMAIL          (default: admin@creuser.local)
///   CREUSER_BOOTSTRAP_PASSWORD       (default: ChangeMe!)
///   CREUSER_BOOTSTRAP_PASSWORD_HASH  (optional, takes precedence over the
///                                     plaintext — useful for production
///                                     deployments where you don't want a
///                                     plaintext admin password in env vars)
/// The seeded user is always created with <c>MustChangePassword=true</c>.
/// </summary>
public sealed class BootstrapAdminService
{
    private readonly IUserStore _users;
    private readonly IPasswordHasher _hasher;
    private readonly IConfiguration _config;
    private readonly ILogger<BootstrapAdminService> _log;

    public BootstrapAdminService(
        IUserStore users,
        IPasswordHasher hasher,
        IConfiguration config,
        ILogger<BootstrapAdminService> log
    )
    {
        _users = users;
        _hasher = hasher;
        _config = config;
        _log = log;
    }

    public async Task EnsureAsync(CancellationToken ct = default)
    {
        if (await _users.CountAsync(ct) > 0)
            return;

        var email = _config["CREUSER_BOOTSTRAP_EMAIL"] ?? "admin@creuser.local";
        var presetHash = _config["CREUSER_BOOTSTRAP_PASSWORD_HASH"];
        var hash = !string.IsNullOrWhiteSpace(presetHash)
            ? presetHash
            : _hasher.Hash(_config["CREUSER_BOOTSTRAP_PASSWORD"] ?? "ChangeMe!");

        var now = DateTime.UtcNow;
        var admin = new User(
            Id: Guid.NewGuid(),
            Email: email,
            DisplayName: "Bootstrap Admin",
            Role: Roles.Admin,
            PasswordHash: hash,
            IsActive: true,
            MustChangePassword: true,
            LastLoginAt: null,
            PasswordChangedAt: null,
            CreatedAt: now,
            UpdatedAt: now
        );
        await _users.SaveAsync(admin, ct);
        _log.LogInformation(
            "Bootstrap admin {Email} created. Sign in and change the password immediately.",
            email
        );
    }
}
