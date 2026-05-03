// The repository name follows the lowercase-table convention used in
// Tables/users.cs to keep the persistence-layer naming consistent (the
// "users-the-table" repository, not "users-the-domain-object").
#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Auth.Abstractions;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class usersRepository : IUserStore
{
    private const string SchemaTable = "cr.users";
    private readonly NpgsqlDataSource _ds;

    public usersRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<users>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE LOWER(email) = LOWER(@email) LIMIT 1",
                new { email },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<users>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE id = @id LIMIT 1",
                new { id },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT COUNT(*) FROM {SchemaTable} WHERE LOWER(email) = LOWER(@email)",
                new { email },
                cancellationToken: ct
            )
        );
        return count > 0;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition($"SELECT COUNT(*) FROM {SchemaTable}", cancellationToken: ct)
        );
    }

    public async Task<int> CountByRoleAsync(
        string role,
        bool activeOnly = true,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var sql = activeOnly
            ? $"SELECT COUNT(*) FROM {SchemaTable} WHERE role = @role AND is_active = TRUE"
            : $"SELECT COUNT(*) FROM {SchemaTable} WHERE role = @role";
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { role }, cancellationToken: ct)
        );
    }

    public async Task<IReadOnlyList<User>> ListAsync(
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<users>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} ORDER BY created_at DESC OFFSET @skip LIMIT @take",
                new { skip, take },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task SaveAsync(User user, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable}
                  (id, email, display_name, role, password_hash, is_active,
                   must_change_password, last_login_at, password_changed_at,
                   created_at, updated_at)
                VALUES
                  (@id, @email, @display_name, @role, @password_hash, @is_active,
                   @must_change_password, @last_login_at, @password_changed_at,
                   @created_at, @updated_at)
                ON CONFLICT (id) DO UPDATE SET
                  email                = EXCLUDED.email,
                  display_name         = EXCLUDED.display_name,
                  role                 = EXCLUDED.role,
                  password_hash        = EXCLUDED.password_hash,
                  is_active            = EXCLUDED.is_active,
                  must_change_password = EXCLUDED.must_change_password,
                  last_login_at        = EXCLUDED.last_login_at,
                  password_changed_at  = EXCLUDED.password_changed_at,
                  updated_at           = CURRENT_TIMESTAMP
                """,
                ToRow(user),
                cancellationToken: ct
            )
        );
    }

    public async Task UpdateLastLoginAsync(Guid id, DateTime when, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"UPDATE {SchemaTable} SET last_login_at = @when, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                new { id, when },
                cancellationToken: ct
            )
        );
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(
                $"DELETE FROM {SchemaTable} WHERE id = @id",
                new { id },
                cancellationToken: ct
            )
        );
        return rows > 0;
    }

    private static User ToDomain(users r) =>
        new(
            r.id,
            r.email,
            r.display_name,
            r.role,
            r.password_hash,
            r.is_active,
            r.must_change_password,
            r.last_login_at,
            r.password_changed_at,
            r.created_at,
            r.updated_at
        );

    private static users ToRow(User u) =>
        new()
        {
            id = u.Id,
            email = u.Email,
            display_name = u.DisplayName,
            role = u.Role,
            password_hash = u.PasswordHash,
            is_active = u.IsActive,
            must_change_password = u.MustChangePassword,
            last_login_at = u.LastLoginAt,
            password_changed_at = u.PasswordChangedAt,
            created_at = u.CreatedAt,
            updated_at = u.UpdatedAt,
        };
}
