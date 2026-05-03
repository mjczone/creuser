namespace Creuser.Core.Execution;

/// <summary>
/// What a step returns from <see cref="IStepRunner.ExecuteAsync"/>. The
/// executor reads this to advance the run, persist audit records, and apply
/// any file mutations transactionally to the workspace working tree.
///
/// <para>
/// Steps do <em>not</em> mutate the working tree directly — they return
/// <see cref="FileChanges"/> and the executor stages + commits them at
/// step end. A step that fails halfway leaves no partial mutation. See
/// architecture.md "File mutation discipline" for the contract.
/// </para>
/// </summary>
public sealed record StepResult(
    StepStatus Status,
    /// <summary>Outputs the step produced. JSON-serializable values only — the executor persists this map verbatim and downstream step bindings reference fields by name.</summary>
    IReadOnlyDictionary<string, object?> Outputs,
    IReadOnlyList<FileChange> FileChanges,
    /// <summary>Sidecar artifacts: stdout, stderr, full LLM transcript, generated files. Persisted under <c>&lt;dataDir&gt;/runs/&lt;runId&gt;/&lt;stepId&gt;/</c>.</summary>
    IReadOnlyList<StepArtifact> Artifacts,
    long DurationMs,
    /// <summary>Tokens consumed by an LLM call. Null for non-LLM steps.</summary>
    long? TokensUsed = null,
    /// <summary>Cost in USD for an LLM call. Null for non-LLM steps.</summary>
    decimal? CostUsd = null,
    /// <summary>Operator-facing error message when <see cref="Status"/> is <see cref="StepStatus.Failed"/>.</summary>
    string? ErrorMessage = null,
    /// <summary>Opaque token saved when the step paused. The executor reschedules the wake-up and re-invokes the runner with this token in <c>StepContext.ResumeToken</c>.</summary>
    string? ResumeToken = null
)
{
    /// <summary>Construct a successful result with no file changes / artifacts. Useful for runners that just compute outputs.</summary>
    public static StepResult Success(
        IReadOnlyDictionary<string, object?> outputs,
        long durationMs,
        long? tokensUsed = null,
        decimal? costUsd = null
    ) =>
        new(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts: Array.Empty<StepArtifact>(),
            DurationMs: durationMs,
            TokensUsed: tokensUsed,
            CostUsd: costUsd
        );

    public static StepResult Failure(string error, long durationMs) =>
        new(
            Status: StepStatus.Failed,
            Outputs: new Dictionary<string, object?>(),
            FileChanges: Array.Empty<FileChange>(),
            Artifacts: Array.Empty<StepArtifact>(),
            DurationMs: durationMs,
            ErrorMessage: error
        );
}

/// <summary>
/// One declared file mutation. The executor stages all changes returned by
/// a step into the workspace working tree at step end and commits them as
/// one atomic operation.
///
/// <para>
/// <see cref="BeforeHash"/> and <see cref="AfterHash"/> are sha256 of the
/// raw bytes — recorded for audit so re-running detects drift, and so a
/// "diff this step" UI doesn't need to re-read the working tree state.
/// </para>
/// </summary>
public sealed record FileChange(
    /// <summary>Path relative to the workspace working tree root.</summary>
    string Path,
    FileChangeOp Op,
    /// <summary>Destination path for <see cref="FileChangeOp.Rename"/>; null otherwise.</summary>
    string? RenameTo = null,
    /// <summary>Sha256 of prior content. Null for <see cref="FileChangeOp.Create"/>.</summary>
    string? BeforeHash = null,
    /// <summary>Sha256 of new content. Null for <see cref="FileChangeOp.Delete"/>.</summary>
    string? AfterHash = null,
    /// <summary>New content for Create/Modify. Null for Delete/Rename.</summary>
    byte[]? Content = null,
    /// <summary>Unified diff text for Modify, when the runner wants to make audit pretty. Optional — the executor can compute it from before/after.</summary>
    string? Diff = null
);

/// <summary>
/// Sidecar artifact produced by a step — captured stdout, stderr, an LLM
/// transcript, a generated file the run wants to keep around for inspection
/// without committing it to the working tree, etc.
/// </summary>
public sealed record StepArtifact(
    /// <summary>Discriminator — <c>stdout</c>, <c>stderr</c>, <c>transcript</c>, <c>generated-file</c>, <c>plan</c>, …</summary>
    string Kind,
    /// <summary>Filename (no directory) the artifact is stored under within the run's artifact directory.</summary>
    string FileName,
    /// <summary>Raw artifact bytes; the executor writes them to disk.</summary>
    byte[] Content,
    string? ContentType = null
);
