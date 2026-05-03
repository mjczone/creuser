using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Projections.Scanner;
using Microsoft.Extensions.Logging;

namespace Creuser.Projections.Sync;

/// <summary>
/// Default <see cref="IProjectionSyncService"/>: loads conventions, runs
/// the scanner, replaces the projection in a single transaction, returns
/// the audit report.
///
/// <para>
/// Failures during the load + scan phase produce a partial report with
/// <c>SchemaFailures</c> / <c>ConventionConflicts</c> populated so the SPA
/// can render where things went wrong. Storage failures bubble up — the
/// transactional replace either succeeds or leaves the prior projection
/// untouched.
/// </para>
/// </summary>
public sealed class ProjectionSyncService : IProjectionSyncService
{
    private readonly IConventionLoader _loader;
    private readonly ProjectionScanner _scanner;
    private readonly IEntityStore _entityStore;
    private readonly ILogger<ProjectionSyncService> _logger;

    public ProjectionSyncService(
        IConventionLoader loader,
        ProjectionScanner scanner,
        IEntityStore entityStore,
        ILogger<ProjectionSyncService> logger
    )
    {
        _loader = loader;
        _scanner = scanner;
        _entityStore = entityStore;
        _logger = logger;
    }

    public async Task<ProjectionReport> RunAsync(
        Workspace workspace,
        string workingTreePath,
        CancellationToken ct = default
    )
    {
        var loadResult = await _loader.LoadAsync(workspace, workingTreePath, ct);
        foreach (var err in loadResult.Errors)
            _logger.LogWarning(
                "Convention load error in {Workspace} ({Source}): {Message}",
                workspace.Slug,
                err.Source ?? "<root>",
                err.Message
            );

        var scan = _scanner.Scan(workspace, workingTreePath, loadResult.Conventions);
        await _entityStore.ReplaceAllAsync(workspace.Id, scan.Entities, scan.Refs, ct);
        return scan.Report;
    }
}
