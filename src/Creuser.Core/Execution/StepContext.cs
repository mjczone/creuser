using Microsoft.Extensions.Logging;

namespace Creuser.Core.Execution;

/// <summary>
/// Per-step execution context. Threads the run identity, the workspace
/// affinity, the working tree path, the budgets, and a logger through to the
/// runner. Runners that need additional services (e.g. <c>SecretsService</c>,
/// <c>AgentClientResolver</c>) get those via constructor injection — the
/// context stays small and intentional.
///
/// <para>
/// <see cref="WorkingTreePath"/> is the absolute path the runner should
/// read from / write to. For git workspaces it points at the
/// <c>WorkspaceFilesystemService</c>-managed clone under
/// <c>&lt;dataDir&gt;/workspaces/&lt;slug&gt;/</c>; for local workspaces it's
/// the operator-configured path. The runner never sees the workspace type;
/// the executor does the right thing.
/// </para>
/// </summary>
public sealed record StepContext(
    Guid RunId,
    Guid WorkspaceId,
    string WorkspaceSlug,
    string WorkingTreePath,
    Guid StepId,
    string StepName,
    StepBudgets Budgets,
    ILogger Logger,
    /// <summary>Per-job allow-list of commands the shell runner may invoke. Empty set blocks every shell command — the operator must explicitly opt in to each binary the script needs.</summary>
    IReadOnlySet<string>? AllowedCommands = null,
    /// <summary>Filenames under <c>/data/secrets/</c> the script's runner is allowed to read. Other filenames return "secret not declared" even if they exist on disk.</summary>
    IReadOnlySet<string>? RequiredSecrets = null,
    /// <summary>Token from a prior <see cref="StepStatus.Paused"/> result, when the step is being resumed. Null on first invocation.</summary>
    string? ResumeToken = null
);

/// <summary>
/// Caps the executor enforces around a step. Runners can read these to
/// adapt behavior (e.g. an LLM step trims the prompt window to fit
/// <see cref="MaxTokens"/>) but the executor is the authority — a runner
/// that exceeds a budget gets cancelled and its result marked failed.
/// </summary>
public sealed record StepBudgets(
    /// <summary>Wall-clock cap on this step's execution. Null means inherit the run-level budget.</summary>
    TimeSpan? MaxDuration = null,
    /// <summary>Token cap for LLM steps. Null means inherit.</summary>
    long? MaxTokens = null,
    /// <summary>Cost cap (USD) for LLM steps. Null means inherit.</summary>
    decimal? MaxCostUsd = null
);
