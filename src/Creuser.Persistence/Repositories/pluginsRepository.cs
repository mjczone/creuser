#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using System.Text.Json;
using Creuser.Core.Repositories;
using Creuser.Persistence.Tables;
using Creuser.Plugins.Loader;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

/// <summary>
/// Persistence for <c>cr.plugins</c>. Discovery output gets written here
/// by <see cref="PluginInitializer"/> on every host startup; rows for
/// plugins no longer present on disk are removed (full replace).
/// </summary>
public sealed class pluginsRepository : IPluginRecordStore
{
    private const string SchemaTable = "cr.plugins";
    private readonly NpgsqlDataSource _ds;

    public pluginsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task ReplaceAllAsync(
        IReadOnlyList<RegisteredPlugin> registered,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    $"DELETE FROM {SchemaTable}",
                    transaction: tx,
                    cancellationToken: ct
                )
            );
            if (registered.Count > 0)
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        $"""
                        INSERT INTO {SchemaTable}
                          (id, name, version, author, description, min_host_version,
                           required_tools, provides, documentation_url, status, status_message, loaded_at)
                        VALUES
                          (@id, @name, @version, @author, @description, @min_host_version,
                           @required_tools::jsonb, @provides::jsonb, @documentation_url, @status, @status_message, @loaded_at)
                        """,
                        registered.Select(ToRow).ToArray(),
                        transaction: tx,
                        cancellationToken: ct
                    )
                );
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<RegisteredPlugin>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<plugins>(
            new CommandDefinition($"SELECT * FROM {SchemaTable} ORDER BY id", cancellationToken: ct)
        );
        return rows.Select(ToDomain).ToList();
    }

    private static RegisteredPlugin ToDomain(plugins r) =>
        new(
            new PluginManifestSnapshot(
                Id: r.id,
                Name: r.name,
                Version: r.version,
                Author: r.author,
                Description: r.description,
                MinimumHostVersion: r.min_host_version,
                RequiredTools: TryDeserializeArray(r.required_tools),
                Provides: TryDeserializeArray(r.provides),
                DocumentationUrl: r.documentation_url
            ),
            r.status,
            r.status_message,
            r.loaded_at
        );

    private static plugins ToRow(RegisteredPlugin p) =>
        new()
        {
            id = p.Manifest.Id,
            name = p.Manifest.Name,
            version = p.Manifest.Version,
            author = p.Manifest.Author,
            description = p.Manifest.Description,
            min_host_version = p.Manifest.MinimumHostVersion,
            required_tools = p.Manifest.RequiredTools is { Count: > 0 } rt
                ? JsonSerializer.Serialize(rt)
                : null,
            provides = p.Manifest.Provides is { Count: > 0 } prov
                ? JsonSerializer.Serialize(prov)
                : null,
            documentation_url = p.Manifest.DocumentationUrl,
            status = p.Status,
            status_message = p.StatusMessage,
            loaded_at = p.LoadedAt,
        };

    private static IReadOnlyList<string>? TryDeserializeArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            return null;
        }
    }
}
