#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Execution;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class schedulesRepository : IScheduleStore
{
    private const string SchemaTable = "cr.schedules";
    private readonly NpgsqlDataSource _ds;

    public schedulesRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<Schedule?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<schedules>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE id = @id LIMIT 1",
                new { id },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<Schedule>> ListByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<schedules>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE workspace_id = @workspaceId ORDER BY created_at DESC",
                new { workspaceId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Schedule>> ListByJobAsync(
        Guid jobScriptId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<schedules>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE job_script_id = @jobScriptId ORDER BY created_at DESC",
                new { jobScriptId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Schedule>> ListDueCronAsync(
        DateTime asOf,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<schedules>(
            new CommandDefinition(
                $"""
                SELECT * FROM {SchemaTable}
                WHERE enabled = true
                  AND kind = 'cron'
                  AND next_due_at IS NOT NULL
                  AND next_due_at <= @asOf
                ORDER BY next_due_at ASC
                """,
                new { asOf },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Schedule>> ListSyncTriggeredAsync(
        Guid workspaceId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<schedules>(
            new CommandDefinition(
                $"""
                SELECT * FROM {SchemaTable}
                WHERE workspace_id = @workspaceId
                  AND kind = 'sync'
                  AND enabled = true
                ORDER BY created_at ASC
                """,
                new { workspaceId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task SaveAsync(Schedule schedule, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable}
                  (id, workspace_id, job_script_id, kind, cron_expression, enabled,
                   next_due_at, last_fired_at, last_run_id, created_at, updated_at, created_by)
                VALUES
                  (@id, @workspace_id, @job_script_id, @kind, @cron_expression, @enabled,
                   @next_due_at, @last_fired_at, @last_run_id, @created_at, @updated_at, @created_by)
                ON CONFLICT (id) DO UPDATE SET
                  kind            = EXCLUDED.kind,
                  cron_expression = EXCLUDED.cron_expression,
                  enabled         = EXCLUDED.enabled,
                  next_due_at     = EXCLUDED.next_due_at,
                  updated_at      = CURRENT_TIMESTAMP
                """,
                ToRow(schedule),
                cancellationToken: ct
            )
        );
    }

    public async Task MarkFiredAsync(
        Guid scheduleId,
        DateTime firedAt,
        DateTime? nextDueAt,
        Guid? runId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE {SchemaTable}
                   SET last_fired_at = @firedAt,
                       next_due_at   = @nextDueAt,
                       last_run_id   = @runId,
                       updated_at    = CURRENT_TIMESTAMP
                 WHERE id = @scheduleId
                """,
                new
                {
                    scheduleId,
                    firedAt,
                    nextDueAt,
                    runId,
                },
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

    private static Schedule ToDomain(schedules r) =>
        new(
            r.id,
            r.workspace_id,
            r.job_script_id,
            r.kind,
            r.cron_expression,
            r.enabled,
            r.next_due_at,
            r.last_fired_at,
            r.last_run_id,
            r.created_at,
            r.updated_at,
            r.created_by
        );

    private static schedules ToRow(Schedule s) =>
        new()
        {
            id = s.Id,
            workspace_id = s.WorkspaceId,
            job_script_id = s.JobScriptId,
            kind = s.Kind,
            cron_expression = s.CronExpression,
            enabled = s.Enabled,
            next_due_at = s.NextDueAt,
            last_fired_at = s.LastFiredAt,
            last_run_id = s.LastRunId,
            created_at = s.CreatedAt,
            updated_at = s.UpdatedAt,
            created_by = s.CreatedBy,
        };
}
