// Repository name follows the lowercase-table convention used in
// Tables/app_settings.cs. See Repositories/usersRepository.cs for the
// rationale.
#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using System.Text.Json;
using Creuser.Persistence.AppSettings;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class appSettingsRepository : IAppSettingsStore
{
    private const string SchemaTable = "cr.app_settings";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _ds;

    public appSettingsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        where T : class
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var json = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                $"SELECT value::text FROM {SchemaTable} WHERE key = @key LIMIT 1",
                new { key },
                cancellationToken: ct
            )
        );
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        Guid? updatedBy,
        CancellationToken ct = default
    )
        where T : class
    {
        var json = JsonSerializer.Serialize(value, JsonOpts);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable} (key, value, updated_at, updated_by)
                VALUES (@key, @value::jsonb, CURRENT_TIMESTAMP, @updated_by)
                ON CONFLICT (key) DO UPDATE SET
                  value      = EXCLUDED.value,
                  updated_at = CURRENT_TIMESTAMP,
                  updated_by = EXCLUDED.updated_by
                """,
                new
                {
                    key,
                    value = json,
                    updated_by = updatedBy,
                },
                cancellationToken: ct
            )
        );
    }
}
