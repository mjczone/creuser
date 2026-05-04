#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Repositories;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class workspacePluginSettingsRepository : IPluginSettingsStore
{
    private const string SchemaTable = "cr.workspace_plugin_settings";
    private readonly NpgsqlDataSource _ds;

    public workspacePluginSettingsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<string?> GetAsync(
        Guid workspaceId,
        string pluginId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                $"SELECT settings::text FROM {SchemaTable} WHERE workspace_id = @workspaceId AND plugin_id = @pluginId",
                new { workspaceId, pluginId },
                cancellationToken: ct
            )
        );
    }

    public async Task SetAsync(
        Guid workspaceId,
        string pluginId,
        string settingsJson,
        Guid? updatedBy,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable}
                  (workspace_id, plugin_id, settings, updated_at, updated_by)
                VALUES
                  (@workspaceId, @pluginId, @settingsJson::jsonb, CURRENT_TIMESTAMP, @updatedBy)
                ON CONFLICT (workspace_id, plugin_id) DO UPDATE SET
                  settings = EXCLUDED.settings,
                  updated_at = CURRENT_TIMESTAMP,
                  updated_by = EXCLUDED.updated_by
                """,
                new
                {
                    workspaceId,
                    pluginId,
                    settingsJson,
                    updatedBy,
                },
                cancellationToken: ct
            )
        );
    }

    public async Task DeleteAsync(Guid workspaceId, string pluginId, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"DELETE FROM {SchemaTable} WHERE workspace_id = @workspaceId AND plugin_id = @pluginId",
                new { workspaceId, pluginId },
                cancellationToken: ct
            )
        );
    }
}
