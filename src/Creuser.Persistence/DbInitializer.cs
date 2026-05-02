using Creuser.Auth.Core;
using Creuser.Persistence.Tables;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MJCZone.DapperMatic;
using Npgsql;

namespace Creuser.Persistence;

/// <summary>
/// Runs at app startup: creates the `cr` schema and DapperMatic-managed
/// tables if they're missing, then ensures a bootstrap admin exists.
/// Idempotent — safe to run on every boot.
/// </summary>
public sealed class DbInitializer : IHostedService
{
    private readonly NpgsqlDataSource _ds;
    private readonly BootstrapAdminService _bootstrap;
    private readonly ILogger<DbInitializer> _log;

    public DbInitializer(
        NpgsqlDataSource ds,
        BootstrapAdminService bootstrap,
        ILogger<DbInitializer> log
    )
    {
        _ds = ds;
        _bootstrap = bootstrap;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsBuildTimeOpenApiGeneration())
        {
            _log.LogDebug("Skipping DB initialization during build-time OpenAPI generation.");
            return;
        }

        _log.LogInformation("Initializing database schema (cr.*)...");

        await using (var conn = await _ds.OpenConnectionAsync(cancellationToken))
        {
            // DapperMatic creates the schema/table if missing; existing tables
            // are left alone (no destructive migrations).
            await conn.CreateSchemaIfNotExistsAsync(
                schemaName: "cr",
                tx: null,
                cancellationToken: cancellationToken
            );
            await conn.CreateTableIfNotExistsAsync<users>(
                tx: null,
                cancellationToken: cancellationToken
            );
        }

        await _bootstrap.EnsureAsync(cancellationToken);

        _log.LogInformation("Database initialization complete.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// True when the host is being constructed by Microsoft.Extensions.ApiDescription.Server's
    /// build-time tool (dotnet-getdocument). The tool starts hosted services, so without
    /// this guard our DB initialization would try to connect to Postgres at build time.
    /// </summary>
    private static bool IsBuildTimeOpenApiGeneration()
    {
        // The build-time tool loads our assembly with itself as the entry
        // assembly. Checking the entry-assembly name catches that reliably,
        // whereas Environment.CommandLine has been observed to lose the
        // "dotnet-getdocument" token in some runtimes.
        var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (
            entryName is not null
            && entryName.Contains("getdocument", StringComparison.OrdinalIgnoreCase)
        )
            return true;
        // Belt-and-suspenders.
        return Environment.CommandLine.Contains(
            "dotnet-getdocument",
            StringComparison.OrdinalIgnoreCase
        );
    }
}
