namespace Creuser.Core.Execution;

/// <summary>
/// One implementation of a step type. The platform's central extension
/// seam — built-in runners (`llm-chat`, `shell`, `csharp`, `file-mutate`,
/// `file-frontmatter`, ...) implement this interface; plugins contribute
/// additional runners for their domain.
///
/// <para>
/// Three contractual properties — see architecture.md "Execution model →
/// IStepRunner contract":
/// <list type="number">
///   <item>No direct file writes. Steps return <see cref="StepResult.FileChanges"/> and the executor applies them transactionally.</item>
///   <item>Budgets are enforced by the host. Runners read <see cref="StepBudgets"/> for adaptive behavior; the executor wraps execution with the actual enforcement.</item>
///   <item>Pause + resume via <see cref="StepResult.ResumeToken"/>. A step that needs to wait sets <see cref="StepStatus.Paused"/> + a token; the executor reschedules and re-invokes with the token in <see cref="StepContext.ResumeToken"/>.</item>
/// </list>
/// </para>
/// </summary>
public interface IStepRunner
{
    /// <summary>Type discriminator referenced from job script frontmatter — e.g. <c>llm-chat</c>, <c>shell</c>, <c>file-mutate</c>.</summary>
    string StepType { get; }

    /// <summary>
    /// Execute the step. Inputs are already resolved (binding from upstream
    /// step outputs, default substitution, parameter merging). The executor
    /// is responsible for cancellation enforcement and timeouts; runners
    /// should respect <paramref name="ct"/>.
    /// </summary>
    Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    );
}
