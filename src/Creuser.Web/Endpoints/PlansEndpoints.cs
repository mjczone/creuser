using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public sealed record PlanSummary(
    Guid Id,
    Guid WorkspaceId,
    Guid JobScriptId,
    string Goal,
    int StepCount,
    string? Reasoning,
    string Model,
    string Provider,
    long? TokensUsed,
    DateTime CreatedAt
);

public sealed record PlanDetail(
    Guid Id,
    Guid WorkspaceId,
    Guid JobScriptId,
    string Goal,
    string StepsJson,
    string? Reasoning,
    string Model,
    string Provider,
    long? TokensUsed,
    DateTime CreatedAt
);

/// <summary>
/// Read endpoints for persisted <see cref="JobPlan"/> records — what each
/// <c>llm-planner</c> step emitted, including the structured step list +
/// the planner's reasoning. Forms the audit trail for plan-then-execute
/// runs.
/// </summary>
public static class PlansEndpoints
{
    public static IEndpointRouteBuilder MapPlansEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/plans")
            .WithTags("Plans")
            .RequireAuthorization();
        group.MapGet("/", (Delegate)List).WithName("ListPlans");
        group.MapGet("/{planId:guid}", (Delegate)Get).WithName("GetPlan");
        return app;
    }

    [AiCapability(
        "plans.list",
        "plans",
        "Workspace plans",
        "Browse the persisted JobPlan records produced by `llm-planner` steps in this workspace. Each plan captures the goal, the structured step list the planner emitted, the reasoning, and the originating job. Useful for auditing plan-then-execute runs and reviewing what the planner decided to do.",
        "list plans",
        "show plans",
        "what plans exist",
        "browse llm-planner output",
        Route = "/w/:slug/plans",
        RequiresRole = Roles.User
    )]
    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<PlanSummary>>>, ProblemHttpResult>
    > List(
        string slug,
        IWorkspaceStore workspaces,
        IJobPlanStore plans,
        int? skip,
        int? take,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug, ct);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);

        var rows = await plans.ListByWorkspaceAsync(ws.Id, skip ?? 0, take ?? 50, ct);
        var summaries = rows.Select(p => new PlanSummary(
                p.Id,
                p.WorkspaceId,
                p.JobScriptId,
                p.Goal,
                CountSteps(p.StepsJson),
                p.Reasoning,
                p.Model,
                p.Provider,
                p.TokensUsed,
                p.CreatedAt
            ))
            .ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<PlanSummary>>(summaries));
    }

    private static async Task<Results<Ok<ApiResult<PlanDetail>>, ProblemHttpResult>> Get(
        string slug,
        Guid planId,
        IWorkspaceStore workspaces,
        IJobPlanStore plans,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug, ct);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);

        var plan = await plans.FindByIdAsync(planId, ct);
        if (plan is null || plan.WorkspaceId != ws.Id)
            return Problems.NotFound($"Plan {planId} not found in workspace {slug}.");

        return TypedResults.Ok(
            new ApiResult<PlanDetail>(
                new PlanDetail(
                    plan.Id,
                    plan.WorkspaceId,
                    plan.JobScriptId,
                    plan.Goal,
                    plan.StepsJson,
                    plan.Reasoning,
                    plan.Model,
                    plan.Provider,
                    plan.TokensUsed,
                    plan.CreatedAt
                )
            )
        );
    }

    private static int CountSteps(string stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
            return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stepsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                return doc.RootElement.GetArrayLength();
        }
        catch
        {
            // best effort
        }
        return 0;
    }
}
