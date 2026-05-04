using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Contracts.Responses;
using Creuser.Web.Schedules;
using Creuser.Web.Workspaces;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

/// <summary>
/// CRUD + manual-fire endpoints for workspace schedules. Schedules are
/// workspace-scoped — each row references one job script and one trigger
/// kind (cron or sync). The `SchedulerService` ticks every 30s and fires
/// any due cron schedules; sync-triggered schedules fire inline from
/// <see cref="WorkspacesEndpoints.Sync"/>.
/// </summary>
public static class SchedulesEndpoints
{
    public static IEndpointRouteBuilder MapSchedulesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/schedules")
            .WithTags("Schedules")
            .RequireAuthorization();

        group.MapGet("/", (Delegate)List).WithName("ListSchedules");
        group
            .MapPost("/", (Delegate)Create)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("CreateSchedule");
        group
            .MapPut("/{scheduleId:guid}", (Delegate)Update)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("UpdateSchedule");
        group
            .MapDelete("/{scheduleId:guid}", (Delegate)Delete)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("DeleteSchedule");
        group
            .MapPost("/{scheduleId:guid}/fire", (Delegate)Fire)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("FireSchedule");

        return app;
    }

    [AiCapability(
        "schedules.list",
        "schedules",
        "Workspace schedules",
        "Browse the cron + post-sync schedules configured for jobs in this workspace. Each entry shows its trigger kind, cron expression, last-fired and next-due times. Schedules drive the platform's self-improving loops.",
        "list schedules",
        "show schedules",
        "what runs when",
        "cron",
        "scheduled jobs",
        Route = "/w/:slug/settings/schedules",
        RequiresRole = Roles.Admin
    )]
    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<ScheduleResult>>>, ProblemHttpResult>
    > List(
        string slug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IScheduleStore schedules,
        IJobScriptStore scripts,
        HttpContext http,
        CancellationToken ct
    )
    {
        var access = await WorkspaceAccess.RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return Problems.WorkspaceNotFound(slug);

        var rows = await schedules.ListByWorkspaceAsync(access.Workspace.Id);
        // Look up job names so the UI can render "[job name] runs at [cron]"
        // without N+1 round-trips. Single in-process join.
        var byScript = new Dictionary<Guid, string>();
        foreach (var r in rows)
        {
            if (byScript.ContainsKey(r.JobScriptId))
                continue;
            var script = await scripts.FindByIdAsync(r.JobScriptId);
            byScript[r.JobScriptId] = script?.Name ?? r.JobScriptId.ToString("N")[..8];
        }
        IReadOnlyList<ScheduleResult> result = rows.Select(s => ToResult(s, byScript)).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<ScheduleResult>>(result));
    }

    [AiCapability(
        "schedules.create",
        "schedules",
        "Schedule a job",
        "Create a new schedule for a job — either cron-driven (`cronExpression`) or post-sync (`kind: sync`). The platform's scheduler will fire the job automatically per the schedule.",
        "schedule job",
        "set up cron",
        "automate",
        "fire after sync",
        "post-sync",
        Route = "/w/:slug/settings/schedules",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<ScheduleResult>>, ProblemHttpResult>> Create(
        string slug,
        CreateScheduleRequest request,
        IValidator<CreateScheduleRequest> validator,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts,
        IScheduleStore schedules,
        TimeProvider time,
        HttpContext http,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Problems.ValidationFailed(AuthEndpoints.ToErrorMap(validation));

        var script = await scripts.FindByIdAsync(request.JobScriptId, ct);
        if (script is null || script.WorkspaceId != ws.Id)
            return Problems.JobScriptNotFound(request.JobScriptId.ToString());

        var now = time.GetUtcNow().UtcDateTime;
        var nextDue =
            string.Equals(request.Kind, ScheduleKind.Cron, StringComparison.Ordinal)
            && request.Enabled
                ? CronEvaluator.ComputeNextDue(request.CronExpression, now)
                : null;

        var schedule = new Schedule(
            Id: Guid.NewGuid(),
            WorkspaceId: ws.Id,
            JobScriptId: script.Id,
            Kind: request.Kind,
            CronExpression: request.CronExpression,
            Enabled: request.Enabled,
            NextDueAt: nextDue,
            LastFiredAt: null,
            LastRunId: null,
            CreatedAt: now,
            UpdatedAt: now,
            CreatedBy: CookieAuthHelpers.GetUserId(http)
        );
        await schedules.SaveAsync(schedule, ct);
        return TypedResults.Ok(
            new ApiResult<ScheduleResult>(
                ToResult(schedule, new Dictionary<Guid, string> { [script.Id] = script.Name })
            )
        );
    }

    private static async Task<Results<Ok<ApiResult<ScheduleResult>>, ProblemHttpResult>> Update(
        string slug,
        Guid scheduleId,
        UpdateScheduleRequest request,
        IValidator<UpdateScheduleRequest> validator,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts,
        IScheduleStore schedules,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return Problems.ValidationFailed(AuthEndpoints.ToErrorMap(validation));

        var existing = await schedules.FindByIdAsync(scheduleId, ct);
        if (existing is null || existing.WorkspaceId != ws.Id)
            return Problems.ScheduleNotFound(scheduleId);

        var now = time.GetUtcNow().UtcDateTime;
        // Recompute next-due whenever cron / enabled changes; re-enabling
        // a paused cron should pick up the *next* future occurrence, not
        // catch up missed firings.
        var nextDue =
            string.Equals(request.Kind, ScheduleKind.Cron, StringComparison.Ordinal)
            && request.Enabled
                ? CronEvaluator.ComputeNextDue(request.CronExpression, now)
                : null;

        var updated = existing with
        {
            Kind = request.Kind,
            CronExpression = request.CronExpression,
            Enabled = request.Enabled,
            NextDueAt = nextDue,
            UpdatedAt = now,
        };
        await schedules.SaveAsync(updated, ct);

        var script = await scripts.FindByIdAsync(updated.JobScriptId, ct);
        var nameMap = new Dictionary<Guid, string>
        {
            [updated.JobScriptId] = script?.Name ?? updated.JobScriptId.ToString("N")[..8],
        };
        return TypedResults.Ok(new ApiResult<ScheduleResult>(ToResult(updated, nameMap)));
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> Delete(
        string slug,
        Guid scheduleId,
        IWorkspaceStore workspaces,
        IScheduleStore schedules
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var existing = await schedules.FindByIdAsync(scheduleId);
        if (existing is null || existing.WorkspaceId != ws.Id)
            return Problems.ScheduleNotFound(scheduleId);
        var deleted = await schedules.DeleteAsync(scheduleId);
        return TypedResults.Ok(new ApiResult<bool>(deleted));
    }

    private static async Task<Results<Ok<ApiResult<Guid?>>, ProblemHttpResult>> Fire(
        string slug,
        Guid scheduleId,
        IWorkspaceStore workspaces,
        IScheduleStore schedules,
        IJobScheduleDispatcher dispatcher,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var existing = await schedules.FindByIdAsync(scheduleId, ct);
        if (existing is null || existing.WorkspaceId != ws.Id)
            return Problems.ScheduleNotFound(scheduleId);

        // Manual fire is awaited so the caller learns the run id (and any
        // immediate failure) rather than the cron-tick fire-and-forget
        // pattern.
        var runId = await dispatcher.DispatchAsync(existing, "manual", ct);
        return TypedResults.Ok(new ApiResult<Guid?>(runId));
    }

    private static ScheduleResult ToResult(Schedule s, IReadOnlyDictionary<Guid, string> jobNames)
    {
        var jobName = jobNames.TryGetValue(s.JobScriptId, out var name)
            ? name
            : s.JobScriptId.ToString("N")[..8];
        return new ScheduleResult(
            ScheduleId: s.Id,
            WorkspaceId: s.WorkspaceId,
            JobScriptId: s.JobScriptId,
            JobName: jobName,
            Kind: s.Kind,
            CronExpression: s.CronExpression,
            Enabled: s.Enabled,
            NextDueAt: s.NextDueAt,
            LastFiredAt: s.LastFiredAt,
            LastRunId: s.LastRunId,
            CreatedAt: s.CreatedAt
        );
    }
}
