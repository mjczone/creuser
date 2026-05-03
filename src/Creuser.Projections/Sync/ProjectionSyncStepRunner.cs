using System.Diagnostics;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace Creuser.Projections.Sync;

/// <summary>
/// <c>type: projection-sync</c> step runner. Drops the entity projection
/// for the active workspace (full transactional rebuild) and emits the
/// resulting <see cref="ProjectionReport"/> as the step's structured
/// outputs. Same shape as any other step — multi-step DAGs can chain
/// <c>projection-sync → llm-tool-loop → file-mutate</c> for a complete
/// "scan → reason → fix" loop.
/// </summary>
public sealed class ProjectionSyncStepRunner : IStepRunner
{
    public string StepType => "projection-sync";

    private readonly IProjectionSyncService _service;
    private readonly IWorkspaceStore _workspaces;
    private readonly ILogger<ProjectionSyncStepRunner> _logger;

    public ProjectionSyncStepRunner(
        IProjectionSyncService service,
        IWorkspaceStore workspaces,
        ILogger<ProjectionSyncStepRunner> logger
    )
    {
        _service = service;
        _workspaces = workspaces;
        _logger = logger;
    }

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(ctx.WorkingTreePath) || !Directory.Exists(ctx.WorkingTreePath))
        {
            sw.Stop();
            return StepResult.Failure(
                "projection-sync requires a workspace working tree on disk. Sync the workspace and retry.",
                sw.ElapsedMilliseconds
            );
        }

        var workspace = await _workspaces.FindByIdAsync(ctx.WorkspaceId, ct);
        if (workspace is null)
        {
            sw.Stop();
            return StepResult.Failure(
                $"Workspace {ctx.WorkspaceId} not found.",
                sw.ElapsedMilliseconds
            );
        }

        ProjectionReport report;
        try
        {
            report = await _service.RunAsync(workspace, ctx.WorkingTreePath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "projection-sync failed for workspace {Slug} ({StepName})",
                workspace.Slug,
                ctx.StepName
            );
            sw.Stop();
            return StepResult.Failure(
                $"projection-sync failed: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }

        sw.Stop();

        var outputs = new Dictionary<string, object?>
        {
            ["entities_total"] = report.EntityTotal,
            ["entities_by_kind"] = report.EntitiesByKind.ToDictionary(
                kv => kv.Key,
                kv => (object?)kv.Value
            ),
            ["refs_resolved"] = report.RefsResolved,
            ["refs_unresolved"] = report.RefsUnresolved,
            ["schema_failures"] = report.SchemaFailures,
            ["convention_conflicts"] = report.ConventionConflicts,
            ["convention_count"] = report.ConventionCount,
            ["scan_duration_ms"] = report.ScanDurationMs,
            ["convention_versions"] = report.ConventionVersions.ToDictionary(
                kv => kv.Key,
                kv => (object?)kv.Value
            ),
        };

        var artifact = new StepArtifact(
            "projection-report",
            "projection-report.json",
            JsonSerializer.SerializeToUtf8Bytes(
                report,
                new JsonSerializerOptions { WriteIndented = true }
            ),
            "application/json"
        );

        return new StepResult(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts: [artifact],
            DurationMs: sw.ElapsedMilliseconds
        );
    }
}
