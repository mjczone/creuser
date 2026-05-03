namespace Creuser.Web.Contracts.Responses;

public sealed record JobScriptResult(
    Guid JobScriptId,
    Guid WorkspaceId,
    string Slug,
    string Name,
    string? Description,
    string Pattern,
    string Frontmatter,
    string Body,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public sealed record JobRunResult(
    Guid RunId,
    Guid JobScriptId,
    Guid WorkspaceId,
    string Status,
    string TriggerKind,
    DateTime StartedAt,
    DateTime? CompletedAt,
    long DurationMs,
    long? TotalTokensUsed,
    decimal? TotalCostUsd,
    string? FailureMessage,
    string? StartCommitSha,
    string? EndCommitSha
);

public sealed record JobRunDetailResult(JobRunResult Run, IReadOnlyList<JobRunStepResult> Steps);

public sealed record JobRunStepResult(
    Guid StepId,
    int Position,
    string StepType,
    string Name,
    string Status,
    string IdempotencyKey,
    Guid? CachedFromStepId,
    /// <summary>Resolved inputs JSON.</summary>
    string InputsJson,
    /// <summary>Outputs JSON, null until completion.</summary>
    string? OutputsJson,
    int FileChangeCount,
    string? CommitSha,
    DateTime StartedAt,
    DateTime? CompletedAt,
    long DurationMs,
    long? TokensUsed,
    decimal? CostUsd,
    string? ErrorMessage
);
