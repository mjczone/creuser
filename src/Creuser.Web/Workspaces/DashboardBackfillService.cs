using Creuser.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Creuser.Web.Workspaces;

/// <summary>
/// Hosted service that seeds default dashboards for any workspace that
/// doesn't already have them. Runs once at startup, walks every
/// <c>cr.workspaces</c> row, and calls
/// <see cref="IDashboardSeeder.SeedDefaultsAsync"/> per workspace.
///
/// <para>
/// Necessary because the create-time seeder is fire-and-forget against
/// new workspaces only — workspaces created before the dashboard
/// composer slice shipped have zero seeded rows. The seeder itself is
/// idempotent (matches on <c>(workspace_id, slug)</c>) so re-running for
/// already-seeded workspaces is a no-op; this service can run on every
/// startup without clobbering user edits.
/// </para>
///
/// <para>
/// Failure mode: per-workspace exceptions are logged and skipped, the
/// rest continue. A bad workspace doesn't block startup.
/// </para>
/// </summary>
public sealed class DashboardBackfillService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DashboardBackfillService> _logger;

    public DashboardBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<DashboardBackfillService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsBuildTimeOpenApiGeneration())
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
            var seeder = scope.ServiceProvider.GetRequiredService<IDashboardSeeder>();

            var rows = await workspaces.ListAsync(0, int.MaxValue, cancellationToken);
            var seeded = 0;
            foreach (var ws in rows)
            {
                try
                {
                    await seeder.SeedDefaultsAsync(ws.Id, ws.CreatedBy, cancellationToken);
                    seeded++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Dashboard backfill failed for workspace {Slug} ({Id})",
                        ws.Slug,
                        ws.Id
                    );
                }
            }
            _logger.LogInformation(
                "Dashboard backfill complete: {Seeded} of {Total} workspaces processed",
                seeded,
                rows.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard backfill service failed at startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsBuildTimeOpenApiGeneration()
    {
        var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return (
                entryName is not null
                && entryName.Contains("getdocument", StringComparison.OrdinalIgnoreCase)
            )
            || System.Environment.CommandLine.Contains(
                "dotnet-getdocument",
                StringComparison.OrdinalIgnoreCase
            );
    }
}
