namespace Creuser.Web.Contracts.Responses;

public sealed record ScheduleResult(
    Guid ScheduleId,
    Guid WorkspaceId,
    Guid JobScriptId,
    string JobName,
    string Kind,
    string? CronExpression,
    bool Enabled,
    DateTime? NextDueAt,
    DateTime? LastFiredAt,
    Guid? LastRunId,
    DateTime CreatedAt
);
