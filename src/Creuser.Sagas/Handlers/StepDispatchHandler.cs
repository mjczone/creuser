using System.Diagnostics;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Sagas.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Creuser.Sagas.Handlers;

/// <summary>
/// Wolverine handler for <see cref="DispatchStep"/>: resolves the
/// configured <see cref="IStepRunner"/> from DI, invokes it, applies any
/// returned <see cref="FileChange"/> records via
/// <see cref="IWorkspaceWorkingTree"/>, persists the
/// <see cref="JobRunStep"/> record, and publishes either
/// <see cref="StepCompleted"/> or <see cref="StepFailed"/> back to the
/// saga. Mirrors the inner loop of the previous in-process
/// <c>JobExecutor.ExecuteOneStepAsync</c> exactly — every behavioural
/// invariant (idempotency key shape, commit message format, file-mutation
/// discipline) is preserved byte-for-byte so existing tests stay valid.
///
/// <para>
/// The handler is intentionally durable-aware: every state transition
/// it makes (save initial step record, save completed step record, append
/// commit) is independently safe to retry. Wolverine's at-least-once
/// delivery may invoke this handler twice for the same
/// <see cref="DispatchStep"/>; the idempotency key + the existing
/// <c>cr.job_run_steps</c> upsert semantics handle the retry without
/// double-execution.
/// </para>
/// </summary>
public sealed class StepDispatchHandler
{
    public static async Task<object> Handle(
        DispatchStep cmd,
        IServiceProvider services,
        IJobRunStore runs,
        IWorkspaceStore workspaces,
        IWorkspaceWorkingTree workingTree,
        IPluginContributions contributions,
        IWorkspacePluginStore workspacePlugins,
        ILogger<StepDispatchHandler> logger,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var stepStartedAt = time.GetUtcNow().UtcDateTime;
        var inputsJson = JsonSerializer.Serialize(cmd.Inputs);
        var inputsHash = Sha256(inputsJson);
        var idempotencyKey = Sha256(cmd.StepName + "|" + cmd.StepType + "|" + inputsHash);

        var stepRecord = new JobRunStep(
            Id: cmd.StepId,
            RunId: cmd.RunId,
            Position: cmd.Position,
            StepType: cmd.StepType,
            Name: cmd.StepName,
            Status: StepStatus.Running,
            IdempotencyKey: idempotencyKey,
            CachedFromStepId: null,
            InputsJson: inputsJson,
            OutputsJson: null,
            InputsHash: inputsHash,
            FileChangeCount: 0,
            CommitSha: null,
            StartedAt: stepStartedAt,
            CompletedAt: null,
            DurationMs: 0,
            TokensUsed: null,
            CostUsd: null,
            ErrorMessage: null,
            ResumeToken: null
        );
        await runs.SaveStepAsync(stepRecord, ct);

        var runner = services.GetKeyedService<IStepRunner>(cmd.StepType);
        if (runner is null)
        {
            var errorMessage =
                $"Unknown step type '{cmd.StepType}'. No registered IStepRunner with that key.";
            var failed = stepRecord with
            {
                Status = StepStatus.Failed,
                ErrorMessage = errorMessage,
                CompletedAt = time.GetUtcNow().UtcDateTime,
            };
            await runs.SaveStepAsync(failed, ct);
            return new StepFailed(cmd.RunId, cmd.StepId, cmd.StepDeclId, errorMessage, 0);
        }

        // Per-workspace plugin enablement gate. If this step type was
        // contributed by a plugin and the plugin isn't enabled for this
        // workspace, fail the step with a clear error before invoking
        // the runner. Built-in step types aren't in the contributions
        // map and pass through unconditionally.
        if (contributions.TryGetStepRunnerPlugin(cmd.StepType, out var pluginId))
        {
            var enabled = await workspacePlugins.IsEnabledAsync(cmd.WorkspaceId, pluginId, ct);
            if (!enabled)
            {
                var errorMessage =
                    $"Step type '{cmd.StepType}' is contributed by plugin '{pluginId}', which is not enabled for this workspace. "
                    + $"Enable the plugin at /w/{cmd.WorkspaceSlug}/settings/plugins.";
                var failed = stepRecord with
                {
                    Status = StepStatus.Failed,
                    ErrorMessage = errorMessage,
                    CompletedAt = time.GetUtcNow().UtcDateTime,
                };
                await runs.SaveStepAsync(failed, ct);
                return new StepFailed(cmd.RunId, cmd.StepId, cmd.StepDeclId, errorMessage, 0);
            }
        }

        var workspace = await workspaces.FindByIdAsync(cmd.WorkspaceId, ct);
        if (workspace is null)
        {
            var errorMessage = $"Workspace {cmd.WorkspaceId} no longer exists.";
            var failed = stepRecord with
            {
                Status = StepStatus.Failed,
                ErrorMessage = errorMessage,
                CompletedAt = time.GetUtcNow().UtcDateTime,
            };
            await runs.SaveStepAsync(failed, ct);
            return new StepFailed(cmd.RunId, cmd.StepId, cmd.StepDeclId, errorMessage, 0);
        }

        var allowedCommands =
            cmd.AllowedCommands is null || cmd.AllowedCommands.Count == 0
                ? null
                : (IReadOnlySet<string>)
                    new HashSet<string>(cmd.AllowedCommands, StringComparer.Ordinal);
        var requiredSecrets =
            cmd.RequiredSecrets is null || cmd.RequiredSecrets.Count == 0
                ? null
                : (IReadOnlySet<string>)
                    new HashSet<string>(cmd.RequiredSecrets, StringComparer.Ordinal);

        var budgets = new StepBudgets(
            MaxDuration: cmd.BudgetMaxDurationSeconds.HasValue
                ? TimeSpan.FromSeconds(cmd.BudgetMaxDurationSeconds.Value)
                : null,
            MaxTokens: cmd.BudgetMaxTokens,
            MaxCostUsd: cmd.BudgetMaxCostUsd
        );

        var ctx = new StepContext(
            RunId: cmd.RunId,
            WorkspaceId: cmd.WorkspaceId,
            WorkspaceSlug: cmd.WorkspaceSlug,
            WorkingTreePath: cmd.WorkingTreePath,
            StepId: cmd.StepId,
            StepName: cmd.StepName,
            Budgets: budgets,
            Logger: services.GetRequiredService<ILoggerFactory>().CreateLogger(cmd.StepType),
            AllowedCommands: allowedCommands,
            RequiredSecrets: requiredSecrets,
            ResumeToken: null
        );

        var sw = Stopwatch.StartNew();
        StepResult result;
        try
        {
            result = await runner.ExecuteAsync(ctx, cmd.Inputs, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Step {StepType} threw during execution", cmd.StepType);
            result = StepResult.Failure(
                $"{ex.GetType().Name}: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }

        // Apply file changes via the working tree (one commit per step).
        string? commitSha = null;
        if (result.Status == StepStatus.Succeeded && result.FileChanges.Count > 0)
        {
            try
            {
                var commitMessage = BuildCommitMessage(cmd.StepName, cmd.RunId, cmd.StepId, result);
                var apply = await workingTree.ApplyAndCommitAsync(
                    workspace,
                    cmd.WorkingTreePath,
                    result.FileChanges,
                    commitMessage,
                    ct
                );
                commitSha = apply.CommitSha;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "ApplyAndCommitAsync failed for step {StepId} of run {RunId}",
                    cmd.StepId,
                    cmd.RunId
                );
                result = result with
                {
                    Status = StepStatus.Failed,
                    ErrorMessage =
                        $"Step ran but applying changes to the working tree failed: {ex.Message}",
                };
            }
        }

        var completed = stepRecord with
        {
            Status = result.Status,
            OutputsJson = JsonSerializer.Serialize(result.Outputs),
            FileChangeCount = result.FileChanges.Count,
            CommitSha = commitSha,
            CompletedAt = time.GetUtcNow().UtcDateTime,
            DurationMs = result.DurationMs,
            TokensUsed = result.TokensUsed,
            CostUsd = result.CostUsd,
            ErrorMessage = result.ErrorMessage,
            ResumeToken = result.ResumeToken,
        };
        await runs.SaveStepAsync(completed, ct);

        if (result.Status == StepStatus.Succeeded)
        {
            return new StepCompleted(
                RunId: cmd.RunId,
                StepId: cmd.StepId,
                StepDeclId: cmd.StepDeclId,
                OutputsJson: completed.OutputsJson ?? "{}",
                FileChangeCount: result.FileChanges.Count,
                CommitSha: commitSha,
                TokensUsed: result.TokensUsed,
                CostUsd: result.CostUsd,
                DurationMs: result.DurationMs
            );
        }

        return new StepFailed(
            RunId: cmd.RunId,
            StepId: cmd.StepId,
            StepDeclId: cmd.StepDeclId,
            ErrorMessage: result.ErrorMessage ?? "Step failed.",
            DurationMs: result.DurationMs
        );
    }

    private static string BuildCommitMessage(
        string stepName,
        Guid runId,
        Guid stepId,
        StepResult result
    )
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[creuser] ").Append(stepName).Append(" (run=").Append(runId.ToString("N")[..8]);
        sb.Append(" step=").Append(stepId.ToString("N")[..8]).Append(')');

        if (result.FileChanges.Count > 0)
        {
            sb.Append('\n').Append('\n').Append("Changed:");
            foreach (var c in result.FileChanges.Take(20))
            {
                sb.Append('\n').Append("- ");
                switch (c.Op)
                {
                    case FileChangeOp.Create:
                        sb.Append("create ").Append(c.Path);
                        break;
                    case FileChangeOp.Modify:
                        sb.Append("modify ").Append(c.Path);
                        break;
                    case FileChangeOp.Delete:
                        sb.Append("delete ").Append(c.Path);
                        break;
                    case FileChangeOp.Rename:
                        sb.Append("rename ").Append(c.Path).Append(" -> ").Append(c.RenameTo);
                        break;
                }
            }
            if (result.FileChanges.Count > 20)
                sb.Append('\n')
                    .Append("- … and ")
                    .Append(result.FileChanges.Count - 20)
                    .Append(" more");
        }
        return sb.ToString();
    }

    private static string Sha256(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
