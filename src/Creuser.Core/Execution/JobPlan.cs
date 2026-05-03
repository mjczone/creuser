namespace Creuser.Core.Execution;

/// <summary>
/// One persisted plan emitted by an <c>llm-planner</c> step. Stored in
/// <c>cr.job_plans</c>; the originating run records its id on
/// <see cref="JobRun.PlanId"/>. The plan-then-execute pattern (see
/// architecture.md "Three execution patterns") materializes here: the
/// planner runs first, persists this record, and the executor walks the
/// planned steps as a continuation of the same run.
///
/// <para>
/// Plans are immutable once written. Re-running a plan from a prior run —
/// "execute plan X again" — produces a new <see cref="JobRun"/> whose
/// <see cref="JobRun.PlanId"/> points at the same <see cref="Id"/>.
/// </para>
/// </summary>
public sealed record JobPlan(
    Guid Id,
    Guid WorkspaceId,
    Guid JobScriptId,
    /// <summary>The goal text the planner was asked to satisfy.</summary>
    string Goal,
    /// <summary>Serialized <see cref="JobPlanStep"/> array. JSONB on disk.</summary>
    string StepsJson,
    /// <summary>Free-text reasoning the planner emitted alongside the steps. Useful audit material.</summary>
    string? Reasoning,
    string Model,
    string Provider,
    long? TokensUsed,
    DateTime CreatedAt
);

/// <summary>
/// One step in a <see cref="JobPlan"/>. Same shape as
/// <c>JobScriptStepDecl</c> in the frontmatter parser — when the executor
/// walks a plan, each <see cref="JobPlanStep"/> is treated identically to
/// a hand-authored DAG step. The contract is symmetric on purpose: a plan
/// is a DAG produced by an LLM rather than typed by a human.
/// </summary>
public sealed record JobPlanStep(
    string Id,
    string? Name,
    string Type,
    IReadOnlyList<string> DependsOn,
    IReadOnlyDictionary<string, object?> Inputs
);

public interface IJobPlanStore
{
    Task<JobPlan?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(JobPlan plan, CancellationToken ct = default);
    Task<IReadOnlyList<JobPlan>> ListByWorkspaceAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<JobPlan>> ListByScriptAsync(
        Guid jobScriptId,
        int skip,
        int take,
        CancellationToken ct = default
    );
}
