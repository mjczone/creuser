#pragma warning disable IDE1006, CA1707, SA1300, SA1308 // naming convention

using Creuser.Core.Execution;
using Creuser.Persistence.Tables;
using Dapper;
using Npgsql;

namespace Creuser.Persistence.Repositories;

public sealed class jobPlansRepository : IJobPlanStore
{
    private const string SchemaTable = "cr.job_plans";
    private readonly NpgsqlDataSource _ds;

    public jobPlansRepository(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public async Task<JobPlan?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<job_plans>(
            new CommandDefinition(
                $"SELECT * FROM {SchemaTable} WHERE id = @id LIMIT 1",
                new { id },
                cancellationToken: ct
            )
        );
        return row is null ? null : ToDomain(row);
    }

    public async Task SaveAsync(JobPlan plan, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO {SchemaTable}
                  (id, workspace_id, job_script_id, goal, steps, reasoning, model, provider, tokens_used, created_at)
                VALUES
                  (@id, @workspace_id, @job_script_id, @goal, @steps::jsonb, @reasoning, @model, @provider, @tokens_used, @created_at)
                ON CONFLICT (id) DO UPDATE SET
                  job_script_id = EXCLUDED.job_script_id,
                  goal          = EXCLUDED.goal,
                  steps         = EXCLUDED.steps,
                  reasoning     = EXCLUDED.reasoning,
                  model         = EXCLUDED.model,
                  provider      = EXCLUDED.provider,
                  tokens_used   = EXCLUDED.tokens_used
                """,
                ToRow(plan),
                cancellationToken: ct
            )
        );
    }

    public async Task<IReadOnlyList<JobPlan>> ListByWorkspaceAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<job_plans>(
            new CommandDefinition(
                $"""
                SELECT * FROM {SchemaTable}
                WHERE workspace_id = @workspaceId
                ORDER BY created_at DESC
                OFFSET @skip LIMIT @take
                """,
                new
                {
                    workspaceId,
                    skip,
                    take = Math.Max(1, Math.Min(500, take)),
                },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<JobPlan>> ListByScriptAsync(
        Guid jobScriptId,
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<job_plans>(
            new CommandDefinition(
                $"""
                SELECT * FROM {SchemaTable}
                WHERE job_script_id = @jobScriptId
                ORDER BY created_at DESC
                OFFSET @skip LIMIT @take
                """,
                new
                {
                    jobScriptId,
                    skip,
                    take = Math.Max(1, Math.Min(500, take)),
                },
                cancellationToken: ct
            )
        );
        return rows.Select(ToDomain).ToList();
    }

    private static JobPlan ToDomain(job_plans r) =>
        new(
            r.id,
            r.workspace_id,
            r.job_script_id,
            r.goal,
            r.steps,
            r.reasoning,
            r.model,
            r.provider,
            r.tokens_used,
            r.created_at
        );

    private static job_plans ToRow(JobPlan p) =>
        new()
        {
            id = p.Id,
            workspace_id = p.WorkspaceId,
            job_script_id = p.JobScriptId,
            goal = p.Goal,
            steps = p.StepsJson,
            reasoning = p.Reasoning,
            model = p.Model,
            provider = p.Provider,
            tokens_used = p.TokensUsed,
            created_at = p.CreatedAt,
        };
}
