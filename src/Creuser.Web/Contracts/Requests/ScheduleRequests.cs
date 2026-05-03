namespace Creuser.Web.Contracts.Requests;

public sealed record CreateScheduleRequest(
    Guid JobScriptId,
    string Kind,
    string? CronExpression,
    bool Enabled
);

public sealed record UpdateScheduleRequest(string Kind, string? CronExpression, bool Enabled);
