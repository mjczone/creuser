#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Repositories;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

/// <summary>
/// Persistence for <c>cr.workspace_plugins</c> — per-workspace enablement
/// of host-loaded plugins. Plugin contributions (step runners, capability
/// providers, tool registries) only become visible to a workspace when
/// there's an enabled row here.
/// </summary>
public sealed class workspacePluginsRepository : IWorkspacePluginStore
{
    private const string SchemaTable = "cr.workspace_plugins";
    private readonly NpgsqlDataSource _ds;

    public workspacePluginsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<IReadOnlyDictionary<string, bool>> ListEnablementAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string plugin_id, bool enabled)>(
            new CommandDefinition(
                $"SELECT plugin_id, enabled FROM {SchemaTable} WHERE workspace_id = @workspaceId",
                new { workspaceId },
                cancellationToken: ct
            )
        );
        return rows.ToDictionary(r => r.plugin_id, r => r.enabled, StringComparer.Ordinal);
    }

    public async Task<bool> IsEnabledAsync(
        Guid workspaceId,
        string pluginId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(
                $"SELECT enabled FROM {SchemaTable} WHERE workspace_id = @workspaceId AND plugin_id = @pluginId",
                new { workspaceId, pluginId },
                cancellationToken: ct
            )
        );
        return row ?? false;
    }

    public async Task SetEnabledAsync(
        Guid workspaceId,
        string pluginId,
        bool enabled,
        Guid? updatedBy,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable} (workspace_id, plugin_id, enabled, updated_at, updated_by)
                VALUES (@workspaceId, @pluginId, @enabled, CURRENT_TIMESTAMP, @updatedBy)
                ON CONFLICT (workspace_id, plugin_id) DO UPDATE SET
                  enabled = EXCLUDED.enabled,
                  updated_at = CURRENT_TIMESTAMP,
                  updated_by = EXCLUDED.updated_by
                """,
                new
                {
                    workspaceId,
                    pluginId,
                    enabled,
                    updatedBy,
                },
                cancellationToken: ct
            )
        );
    }
}
