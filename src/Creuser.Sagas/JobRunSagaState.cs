using Creuser.Core.Execution;
using Creuser.Scripting;

namespace Creuser.Sagas;

/// <summary>
/// Persisted saga state for one in-flight <see cref="JobRun"/>. Stored as
/// a Marten document via Wolverine's Marten saga integration. Holds
/// everything needed to advance the run when a step completes:
///
/// <list type="bullet">
/// <item>The <see cref="Steps"/> list (source-of-truth for the DAG to walk).</item>
/// <item><see cref="StepStatuses"/> — what's done / pending / cancelled.</item>
/// <item><see cref="StepOutputs"/> — feeds binding resolution for downstream steps.</item>
/// <item><see cref="Parameters"/> — the run's input parameters, used in <c>$params.X</c> bindings.</item>
/// <item>Run-level totals + commit SHA tracking so the final <see cref="JobRunCompleted"/>
/// event carries accurate aggregate numbers.</item>
/// </list>
///
/// <para>
/// Marten persists the document via the Wolverine saga integration; the
/// "Id" field is the run id. When the saga completes, Wolverine deletes
/// the document — the Marten event stream is the durable history, the
/// saga doc is just live state.
/// </para>
/// </summary>
public sealed class JobRunSagaState
{
    /// <summary>Saga id — same as the run id so lookups are by-run-id.</summary>
    public Guid Id { get; set; }
    public Guid JobScriptId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string WorkspaceSlug { get; set; } = string.Empty;
    public string WorkingTreePath { get; set; } = string.Empty;
    public string TriggerKind { get; set; } = "manual";
    public Guid? TriggeredBy { get; set; }
    public string Status { get; set; } = "running";
    public DateTime StartedAt { get; set; }
    public string? StartCommitSha { get; set; }

    /// <summary>JSON-serialized step decls. Stored as JSON so Marten doesn't need to know our types.</summary>
    public string StepsJson { get; set; } = "[]";

    /// <summary>JSON-serialized initial run parameters (for $params bindings).</summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>Map of step decl id → status string. Walked + mutated as steps complete.</summary>
    public Dictionary<string, string> StepStatuses { get; set; } = new();

    /// <summary>Map of step decl id → step's outputs JSON. Feeds binding resolution.</summary>
    public Dictionary<string, string> StepOutputsJson { get; set; } = new();

    /// <summary>Map of step decl id → the persisted JobRunStep id (so we can update the right row when a step completes).</summary>
    public Dictionary<string, Guid> StepRowIds { get; set; } = new();

    /// <summary>Map of step decl id → position in the audit timeline (assigned at dispatch time).</summary>
    public Dictionary<string, int> StepPositions { get; set; } = new();

    /// <summary>Next position to assign — increments as steps are dispatched.</summary>
    public int NextPosition { get; set; }

    public long? TotalTokensUsed { get; set; }
    public decimal? TotalCostUsd { get; set; }
    public string? LastCommitSha { get; set; }
    public string? FailureMessage { get; set; }
    public Guid? PlanId { get; set; }

    /// <summary>True after the planner step has run + the saga has hydrated plan steps in. Used by <see cref="JobRunSaga"/> to distinguish first-completion vs. plan-step-completion of the planner step.</summary>
    public bool PlannerHydrated { get; set; }

    public IReadOnlyList<JobScriptStepDecl> DeserializeSteps() =>
        System.Text.Json.JsonSerializer.Deserialize<List<JobScriptStepDecl>>(StepsJson)
        ?? new List<JobScriptStepDecl>();

    public IReadOnlyDictionary<string, object?> DeserializeParameters() =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(ParametersJson)
        ?? new Dictionary<string, object?>();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> DeserializeOutputs()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(
            StringComparer.Ordinal
        );
        foreach (var (id, json) in StepOutputsJson)
        {
            try
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    json
                );
                if (dict is not null)
                    result[id] = dict;
            }
            catch
            {
                // best effort — corrupt outputs become an empty dict
            }
        }
        return result;
    }
}
