#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Execution;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class jobRunsRepository : IJobRunStore
{
    private const string RunsTable = "cr.job_runs";
    private const string StepsTable = "cr.job_run_steps";
    private readonly NpgsqlDataSource _ds;

    public jobRunsRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<JobRun?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<job_runs>(
            new CommandDefinition(
                $"SELECT * FROM {RunsTable} WHERE id = @id LIMIT 1",
                new { id },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToRunDomain(row);
    }

    public async Task<IReadOnlyList<JobRun>> ListByWorkspaceAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<job_runs>(
            new CommandDefinition(
                $"""
                SELECT * FROM {RunsTable}
                WHERE workspace_id = @workspaceId
                ORDER BY started_at DESC
                OFFSET @skip LIMIT @take
                """,
                new
                {
                    workspaceId,
                    skip,
                    take,
                },
                cancellationToken: ct
            )
        );
        return rows.Select(ToRunDomain).ToList();
    }

    public async Task<IReadOnlyList<JobRun>> ListByScriptAsync(
        Guid scriptId,
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<job_runs>(
            new CommandDefinition(
                $"""
                SELECT * FROM {RunsTable}
                WHERE job_script_id = @scriptId
                ORDER BY started_at DESC
                OFFSET @skip LIMIT @take
                """,
                new
                {
                    scriptId,
                    skip,
                    take,
                },
                cancellationToken: ct
            )
        );
        return rows.Select(ToRunDomain).ToList();
    }

    public async Task SaveRunAsync(JobRun run, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {RunsTable}
                  (id, job_script_id, workspace_id, status, parameters, start_commit_sha,
                   end_commit_sha, started_at, completed_at, triggered_by, trigger_kind,
                   predecessor_run_id, plan_id, failure_message, total_tokens_used,
                   total_cost_usd, duration_ms)
                VALUES
                  (@id, @job_script_id, @workspace_id, @status, @parameters::jsonb, @start_commit_sha,
                   @end_commit_sha, @started_at, @completed_at, @triggered_by, @trigger_kind,
                   @predecessor_run_id, @plan_id, @failure_message, @total_tokens_used,
                   @total_cost_usd, @duration_ms)
                ON CONFLICT (id) DO UPDATE SET
                  status              = EXCLUDED.status,
                  end_commit_sha      = EXCLUDED.end_commit_sha,
                  completed_at        = EXCLUDED.completed_at,
                  failure_message     = EXCLUDED.failure_message,
                  total_tokens_used   = EXCLUDED.total_tokens_used,
                  total_cost_usd      = EXCLUDED.total_cost_usd,
                  duration_ms         = EXCLUDED.duration_ms
                """,
                ToRunRow(run),
                cancellationToken: ct
            )
        );
    }

    public async Task SaveStepAsync(JobRunStep step, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {StepsTable}
                  (id, run_id, position, step_type, name, status, idempotency_key,
                   cached_from_step_id, inputs, outputs, inputs_hash, file_change_count,
                   commit_sha, started_at, completed_at, duration_ms, tokens_used,
                   cost_usd, error_message, resume_token)
                VALUES
                  (@id, @run_id, @position, @step_type, @name, @status, @idempotency_key,
                   @cached_from_step_id, @inputs::jsonb, @outputs::jsonb, @inputs_hash, @file_change_count,
                   @commit_sha, @started_at, @completed_at, @duration_ms, @tokens_used,
                   @cost_usd, @error_message, @resume_token)
                ON CONFLICT (id) DO UPDATE SET
                  status              = EXCLUDED.status,
                  cached_from_step_id = EXCLUDED.cached_from_step_id,
                  outputs             = EXCLUDED.outputs,
                  file_change_count   = EXCLUDED.file_change_count,
                  commit_sha          = EXCLUDED.commit_sha,
                  completed_at        = EXCLUDED.completed_at,
                  duration_ms         = EXCLUDED.duration_ms,
                  tokens_used         = EXCLUDED.tokens_used,
                  cost_usd            = EXCLUDED.cost_usd,
                  error_message       = EXCLUDED.error_message,
                  resume_token        = EXCLUDED.resume_token
                """,
                ToStepRow(step),
                cancellationToken: ct
            )
        );
    }

    public async Task<IReadOnlyList<JobRunStep>> ListStepsAsync(
        Guid runId,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<job_run_steps>(
            new CommandDefinition(
                $"""
                SELECT * FROM {StepsTable}
                WHERE run_id = @runId
                ORDER BY position ASC
                """,
                new { runId },
                cancellationToken: ct
            )
        );
        return rows.Select(ToStepDomain).ToList();
    }

    private static JobRun ToRunDomain(job_runs r) =>
        new(
            r.id,
            r.job_script_id,
            r.workspace_id,
            Enum.Parse<JobRunStatus>(r.status, ignoreCase: true),
            r.parameters,
            r.start_commit_sha,
            r.end_commit_sha,
            r.started_at,
            r.completed_at,
            r.triggered_by,
            r.trigger_kind,
            r.predecessor_run_id,
            r.plan_id,
            r.failure_message,
            r.total_tokens_used,
            r.total_cost_usd,
            r.duration_ms
        );

    private static job_runs ToRunRow(JobRun run) =>
        new()
        {
            id = run.Id,
            job_script_id = run.JobScriptId,
            workspace_id = run.WorkspaceId,
            status = run.Status.ToString().ToLowerInvariant(),
            parameters = run.ParametersJson,
            start_commit_sha = run.StartCommitSha,
            end_commit_sha = run.EndCommitSha,
            started_at = run.StartedAt,
            completed_at = run.CompletedAt,
            triggered_by = run.TriggeredBy,
            trigger_kind = run.TriggerKind,
            predecessor_run_id = run.PredecessorRunId,
            plan_id = run.PlanId,
            failure_message = run.FailureMessage,
            total_tokens_used = run.TotalTokensUsed,
            total_cost_usd = run.TotalCostUsd,
            duration_ms = run.DurationMs,
        };

    private static JobRunStep ToStepDomain(job_run_steps r) =>
        new(
            r.id,
            r.run_id,
            r.position,
            r.step_type,
            r.name,
            Enum.Parse<StepStatus>(r.status, ignoreCase: true),
            r.idempotency_key,
            r.cached_from_step_id,
            r.inputs,
            r.outputs,
            r.inputs_hash,
            r.file_change_count,
            r.commit_sha,
            r.started_at,
            r.completed_at,
            r.duration_ms,
            r.tokens_used,
            r.cost_usd,
            r.error_message,
            r.resume_token
        );

    private static job_run_steps ToStepRow(JobRunStep s) =>
        new()
        {
            id = s.Id,
            run_id = s.RunId,
            position = s.Position,
            step_type = s.StepType,
            name = s.Name,
            status = s.Status.ToString().ToLowerInvariant(),
            idempotency_key = s.IdempotencyKey,
            cached_from_step_id = s.CachedFromStepId,
            inputs = s.InputsJson,
            outputs = s.OutputsJson,
            inputs_hash = s.InputsHash,
            file_change_count = s.FileChangeCount,
            commit_sha = s.CommitSha,
            started_at = s.StartedAt,
            completed_at = s.CompletedAt,
            duration_ms = s.DurationMs,
            tokens_used = s.TokensUsed,
            cost_usd = s.CostUsd,
            error_message = s.ErrorMessage,
            resume_token = s.ResumeToken,
        };
}
