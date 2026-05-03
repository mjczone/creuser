namespace Creuser.Core.Execution;

/// <summary>
/// Outcome of a single step's execution within a run.
/// </summary>
public enum StepStatus
{
    /// <summary>Step has been declared but the executor hasn't run it yet.</summary>
    Pending,

    /// <summary>Step is currently executing.</summary>
    Running,

    /// <summary>Step completed normally and produced outputs.</summary>
    Succeeded,

    /// <summary>Step's idempotency key matched a prior successful run; outputs were copied without re-execution.</summary>
    Skipped,

    /// <summary>Step suspended itself with a <c>ResumeToken</c> waiting for an external signal (rate-limit, human approval, scheduled wake-up).</summary>
    Paused,

    /// <summary>Step threw or returned an error result.</summary>
    Failed,

    /// <summary>Step never ran because an upstream step failed and downstream propagation cancelled it.</summary>
    Cancelled,
}

/// <summary>
/// Outcome of a whole run.
/// </summary>
public enum JobRunStatus
{
    Pending,
    Running,
    Paused,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// The three execution patterns a Job can declare. The pattern is chosen at
/// design time and persisted on the <see cref="JobScript"/>; the executor
/// dispatches accordingly.
/// </summary>
public static class JobPattern
{
    /// <summary>Fixed DAG known at design time. Inputs flow between steps via declared bindings.</summary>
    public const string Deterministic = "deterministic";

    /// <summary>First step is an <c>llm-planner</c> that emits a structured <see cref="JobPlan"/>; the rest of the run executes that plan.</summary>
    public const string PlanThenExecute = "plan-then-execute";

    /// <summary>An <c>llm-tool-loop</c> step is given a goal + tools + budgets and explores. Bounded; every tool call is recorded.</summary>
    public const string Agentic = "agentic";

    public static bool IsValid(string pattern) =>
        pattern is Deterministic or PlanThenExecute or Agentic;
}

/// <summary>
/// File mutation operations a step can declare. Applied transactionally by
/// the executor at step end — see architecture.md "File mutation discipline".
/// </summary>
public enum FileChangeOp
{
    Create,
    Modify,
    Delete,
    Rename,
}

/// <summary>
/// Job script lifecycle. Drafts can be edited; active jobs run on schedule;
/// disabled jobs ignore triggers but stay editable.
/// </summary>
public static class JobScriptStatus
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Disabled = "disabled";

    public static bool IsValid(string status) => status is Draft or Active or Disabled;
}
