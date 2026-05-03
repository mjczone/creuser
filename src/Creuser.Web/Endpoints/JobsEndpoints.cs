using System.Text.Json;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Scripting;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Contracts.Responses;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

/// <summary>
/// Workspace-scoped CRUD for job scripts plus the run-trigger + run-history
/// surface. The execution model is detailed in architecture.md "Execution
/// model" — this file is the HTTP surface over <see cref="JobExecutor"/>
/// and the audit stores.
/// </summary>
public static class JobsEndpoints
{
    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/jobs")
            .WithTags("Jobs")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", (Delegate)List).WithName("ListJobs");
        group.MapGet("/{jobId:guid}", (Delegate)Get).WithName("GetJob");
        group.MapPost("/", (Delegate)Create).WithName("CreateJob");
        group.MapPut("/{jobId:guid}", (Delegate)Update).WithName("UpdateJob");
        group.MapDelete("/{jobId:guid}", (Delegate)Delete).WithName("DeleteJob");
        group.MapPost("/{jobId:guid}/run", (Delegate)Run).WithName("RunJob");
        group.MapGet("/{jobId:guid}/runs", (Delegate)ListRunsByJob).WithName("ListJobRuns");

        var runs = app.MapGroup("/api/workspaces/{slug}/runs")
            .WithTags("Jobs")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
        runs.MapGet("/", (Delegate)ListRunsByWorkspace).WithName("ListWorkspaceRuns");
        runs.MapGet("/{runId:guid}", (Delegate)GetRun).WithName("GetRun");

        return app;
    }

    [AiCapability(
        "jobs.list",
        "jobs",
        "Job scripts",
        "Browse the list of automation scripts the workspace has — scheduled or on-demand. Each job composes one or more steps (LLM calls, scripts, file mutations) into a Run.",
        "list jobs",
        "show jobs",
        "what jobs",
        "list scripts",
        "what scripts",
        Route = "/w/:slug/settings/jobs",
        RequiresRole = Roles.Admin
    )]
    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<JobScriptResult>>>, ProblemHttpResult>
    > List(string slug, IWorkspaceStore workspaces, IJobScriptStore scripts)
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var rows = await scripts.ListByWorkspaceAsync(ws.Id, skip: 0, take: 200);
        IReadOnlyList<JobScriptResult> result = rows.Select(ToResult).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<JobScriptResult>>(result));
    }

    private static async Task<Results<Ok<ApiResult<JobScriptResult>>, ProblemHttpResult>> Get(
        string slug,
        Guid jobId,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var script = await scripts.FindByIdAsync(jobId);
        if (script is null || script.WorkspaceId != ws.Id)
            return Problems.JobScriptNotFound(jobId.ToString());
        return TypedResults.Ok(new ApiResult<JobScriptResult>(ToResult(script)));
    }

    [AiCapability(
        "jobs.create",
        "jobs",
        "Create a new job",
        "Author a new job script for this workspace. Job scripts compose steps (LLM calls, scripts, file mutations) and run on demand or on a schedule.",
        "create job",
        "new job",
        "add job",
        "create script",
        "new script",
        "automation",
        Route = "/w/:slug/settings/jobs",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<JobScriptResult>>, ProblemHttpResult>> Create(
        string slug,
        CreateJobScriptRequest request,
        IValidator<CreateJobScriptRequest> validator,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts,
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

        if (await scripts.SlugExistsAsync(ws.Id, request.Slug, ct))
            return Problems.JobScriptSlugAlreadyExists(request.Slug);

        var now = time.GetUtcNow().UtcDateTime;
        var script = new JobScript(
            Id: Guid.NewGuid(),
            WorkspaceId: ws.Id,
            Slug: request.Slug,
            Name: request.Name,
            Description: request.Description,
            Pattern: request.Pattern,
            Frontmatter: request.Frontmatter,
            Body: request.Body,
            Status: request.Status,
            CreatedAt: now,
            UpdatedAt: now,
            CreatedBy: CookieAuthHelpers.GetUserId(http)
        );
        await scripts.SaveAsync(script, ct);
        return TypedResults.Ok(new ApiResult<JobScriptResult>(ToResult(script)));
    }

    private static async Task<Results<Ok<ApiResult<JobScriptResult>>, ProblemHttpResult>> Update(
        string slug,
        Guid jobId,
        UpdateJobScriptRequest request,
        IValidator<UpdateJobScriptRequest> validator,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts,
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

        var existing = await scripts.FindByIdAsync(jobId, ct);
        if (existing is null || existing.WorkspaceId != ws.Id)
            return Problems.JobScriptNotFound(jobId.ToString());

        var updated = existing with
        {
            Name = request.Name,
            Description = request.Description,
            Pattern = request.Pattern,
            Frontmatter = request.Frontmatter,
            Body = request.Body,
            Status = request.Status,
            UpdatedAt = time.GetUtcNow().UtcDateTime,
        };
        await scripts.SaveAsync(updated, ct);
        return TypedResults.Ok(new ApiResult<JobScriptResult>(ToResult(updated)));
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> Delete(
        string slug,
        Guid jobId,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var existing = await scripts.FindByIdAsync(jobId);
        if (existing is null || existing.WorkspaceId != ws.Id)
            return Problems.JobScriptNotFound(jobId.ToString());
        var deleted = await scripts.DeleteAsync(jobId);
        return TypedResults.Ok(new ApiResult<bool>(deleted));
    }

    [AiCapability(
        "jobs.run",
        "jobs",
        "Run a job",
        "Trigger an on-demand execution of a job script. The Run is recorded in the audit log with per-step inputs / outputs / token usage; replay via the Run inspector.",
        "run job",
        "execute job",
        "trigger job",
        "run script",
        "execute script",
        Route = "/w/:slug/settings/jobs",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<JobRunResult>>, ProblemHttpResult>> Run(
        string slug,
        Guid jobId,
        RunJobScriptRequest? request,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts,
        JobExecutor executor,
        HttpContext http,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var script = await scripts.FindByIdAsync(jobId, ct);
        if (script is null || script.WorkspaceId != ws.Id)
            return Problems.JobScriptNotFound(jobId.ToString());

        var parameters = request?.Parameters ?? new Dictionary<string, object?>();
        var run = await executor.ExecuteAsync(
            script.Id,
            parameters as IReadOnlyDictionary<string, object?>
                ?? new Dictionary<string, object?>(parameters),
            triggeredBy: CookieAuthHelpers.GetUserId(http),
            triggerKind: "manual",
            ct: ct
        );
        return TypedResults.Ok(new ApiResult<JobRunResult>(ToRunResult(run)));
    }

    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<JobRunResult>>>, ProblemHttpResult>
    > ListRunsByJob(
        string slug,
        Guid jobId,
        IWorkspaceStore workspaces,
        IJobScriptStore scripts,
        IJobRunStore runs
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var script = await scripts.FindByIdAsync(jobId);
        if (script is null || script.WorkspaceId != ws.Id)
            return Problems.JobScriptNotFound(jobId.ToString());
        var rows = await runs.ListByScriptAsync(script.Id, skip: 0, take: 100);
        IReadOnlyList<JobRunResult> result = rows.Select(ToRunResult).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<JobRunResult>>(result));
    }

    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<JobRunResult>>>, ProblemHttpResult>
    > ListRunsByWorkspace(string slug, IWorkspaceStore workspaces, IJobRunStore runs)
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var rows = await runs.ListByWorkspaceAsync(ws.Id, skip: 0, take: 100);
        IReadOnlyList<JobRunResult> result = rows.Select(ToRunResult).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<JobRunResult>>(result));
    }

    private static async Task<Results<Ok<ApiResult<JobRunDetailResult>>, ProblemHttpResult>> GetRun(
        string slug,
        Guid runId,
        IWorkspaceStore workspaces,
        IJobRunStore runs
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var run = await runs.FindByIdAsync(runId);
        if (run is null || run.WorkspaceId != ws.Id)
            return Problems.JobRunNotFound(runId);
        var steps = await runs.ListStepsAsync(runId);
        return TypedResults.Ok(
            new ApiResult<JobRunDetailResult>(
                new JobRunDetailResult(ToRunResult(run), steps.Select(ToStepResult).ToList())
            )
        );
    }

    private static JobScriptResult ToResult(JobScript s) =>
        new(
            JobScriptId: s.Id,
            WorkspaceId: s.WorkspaceId,
            Slug: s.Slug,
            Name: s.Name,
            Description: s.Description,
            Pattern: s.Pattern,
            Frontmatter: s.Frontmatter,
            Body: s.Body,
            Status: s.Status,
            CreatedAt: s.CreatedAt,
            UpdatedAt: s.UpdatedAt
        );

    private static JobRunResult ToRunResult(JobRun r) =>
        new(
            RunId: r.Id,
            JobScriptId: r.JobScriptId,
            WorkspaceId: r.WorkspaceId,
            Status: r.Status.ToString().ToLowerInvariant(),
            TriggerKind: r.TriggerKind,
            StartedAt: r.StartedAt,
            CompletedAt: r.CompletedAt,
            DurationMs: r.DurationMs,
            TotalTokensUsed: r.TotalTokensUsed,
            TotalCostUsd: r.TotalCostUsd,
            FailureMessage: r.FailureMessage,
            StartCommitSha: r.StartCommitSha,
            EndCommitSha: r.EndCommitSha,
            PlanId: r.PlanId
        );

    private static JobRunStepResult ToStepResult(JobRunStep s) =>
        new(
            StepId: s.Id,
            Position: s.Position,
            StepType: s.StepType,
            Name: s.Name,
            Status: s.Status.ToString().ToLowerInvariant(),
            IdempotencyKey: s.IdempotencyKey,
            CachedFromStepId: s.CachedFromStepId,
            InputsJson: s.InputsJson,
            OutputsJson: s.OutputsJson,
            FileChangeCount: s.FileChangeCount,
            CommitSha: s.CommitSha,
            StartedAt: s.StartedAt,
            CompletedAt: s.CompletedAt,
            DurationMs: s.DurationMs,
            TokensUsed: s.TokensUsed,
            CostUsd: s.CostUsd,
            ErrorMessage: s.ErrorMessage
        );
}
