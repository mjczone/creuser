using System.Text.Json;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Repositories;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

/// <summary>
/// CRUD endpoints for workspace dashboards + dashboard groups. Reads are
/// available to any workspace member (admins implicit); writes require
/// editor role. The nav-tree endpoint is what the SPA's icon-bar fetches
/// once on workspace entry.
/// </summary>
public static class DashboardsEndpoints
{
    public static IEndpointRouteBuilder MapDashboardsEndpoints(this IEndpointRouteBuilder app)
    {
        var dashGroup = app.MapGroup("/api/workspaces/{slug}/dashboards")
            .WithTags("Dashboards")
            .RequireAuthorization();

        dashGroup.MapGet("/", (Delegate)ListNavTree).WithName("ListDashboards");
        dashGroup.MapGet("/{dashSlug}", (Delegate)Get).WithName("GetDashboard");
        dashGroup
            .MapPost("/", (Delegate)Create)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("CreateDashboard");
        dashGroup
            .MapPut("/{dashSlug}", (Delegate)Update)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("UpdateDashboard");
        dashGroup
            .MapDelete("/{dashSlug}", (Delegate)Delete)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("DeleteDashboard");

        var groupGroup = app.MapGroup("/api/workspaces/{slug}/dashboard-groups")
            .WithTags("DashboardGroups")
            .RequireAuthorization();

        groupGroup.MapGet("/", (Delegate)ListGroups).WithName("ListDashboardGroups");
        groupGroup
            .MapPost("/", (Delegate)CreateGroup)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("CreateDashboardGroup");
        groupGroup
            .MapPut("/{groupSlug}", (Delegate)UpdateGroup)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("UpdateDashboardGroup");
        groupGroup
            .MapDelete("/{groupSlug}", (Delegate)DeleteGroup)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithName("DeleteDashboardGroup");

        return app;
    }

    // ============================================================
    // Dashboards
    // ============================================================

    [AiCapability(
        "dashboards.list",
        "dashboards",
        "Workspace dashboards",
        "Browse the dashboards configured for this workspace. The SPA's icon bar reads this list on workspace entry — Home is always present, and admins can create standalone dashboards (own icon) or grouped dashboards (sub-sidebar under a group icon).",
        "list dashboards",
        "show dashboards",
        "what dashboards",
        Route = "/w/:slug",
        RequiresRole = Roles.User
    )]
    private static async Task<
        Results<Ok<ApiResult<DashboardNavTree>>, ProblemHttpResult>
    > ListNavTree(string slug, IWorkspaceStore workspaces, IDashboardStore dashboards)
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var tree = await dashboards.GetNavTreeAsync(ws.Id);
        return TypedResults.Ok(new ApiResult<DashboardNavTree>(tree));
    }

    private static async Task<Results<Ok<ApiResult<DashboardResult>>, ProblemHttpResult>> Get(
        string slug,
        string dashSlug,
        IWorkspaceStore workspaces,
        IDashboardStore dashboards
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var dash = await dashboards.FindBySlugAsync(ws.Id, dashSlug);
        if (dash is null)
            return Problems.NotFound($"Dashboard '{dashSlug}' not found.");
        return TypedResults.Ok(new ApiResult<DashboardResult>(ToResult(dash)));
    }

    private static async Task<Results<Ok<ApiResult<DashboardResult>>, ProblemHttpResult>> Create(
        string slug,
        CreateDashboardRequest request,
        IWorkspaceStore workspaces,
        IDashboardStore dashboards,
        HttpContext http
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        if (string.IsNullOrWhiteSpace(request.Slug))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["slug"] = new[] { "slug is required." } }
            );
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["name"] = new[] { "name is required." } }
            );
        if (await dashboards.FindBySlugAsync(ws.Id, request.Slug) is not null)
            return Problems.SlugAlreadyExists(request.Slug);

        Guid? groupId = null;
        if (!string.IsNullOrWhiteSpace(request.GroupSlug))
        {
            var grp = await dashboards.FindGroupBySlugAsync(ws.Id, request.GroupSlug);
            if (grp is null)
                return Problems.NotFound($"Dashboard group '{request.GroupSlug}' not found.");
            groupId = grp.Id;
        }

        var now = DateTime.UtcNow;
        var dash = new Dashboard(
            Id: Guid.Empty,
            WorkspaceId: ws.Id,
            GroupId: groupId,
            Slug: request.Slug,
            Name: request.Name,
            Icon: request.Icon,
            LayoutJson: "{}",
            WidgetsJson: "[]",
            Position: request.Position ?? 100,
            IsDefault: false,
            CreatedAt: now,
            UpdatedAt: now,
            CreatedBy: CookieAuthHelpers.GetUserId(http)
        );
        var created = await dashboards.CreateAsync(dash);
        return TypedResults.Ok(new ApiResult<DashboardResult>(ToResult(created)));
    }

    private static async Task<Results<Ok<ApiResult<DashboardResult>>, ProblemHttpResult>> Update(
        string slug,
        string dashSlug,
        UpdateDashboardRequest request,
        IWorkspaceStore workspaces,
        IDashboardStore dashboards
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var existing = await dashboards.FindBySlugAsync(ws.Id, dashSlug);
        if (existing is null)
            return Problems.NotFound($"Dashboard '{dashSlug}' not found.");

        // Validate JSON shape — we don't enforce a schema here yet (widgets
        // schemas are SPA-side in v1) but we round-trip as JSON to catch
        // malformed payloads early.
        if (!IsJson(request.LayoutJson))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["layoutJson"] = new[] { "layoutJson must be valid JSON." },
                }
            );
        if (!IsJson(request.WidgetsJson))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["widgetsJson"] = new[] { "widgetsJson must be valid JSON." },
                }
            );

        Guid? groupId = existing.GroupId;
        if (request.GroupSlug is not null)
        {
            if (string.IsNullOrWhiteSpace(request.GroupSlug))
            {
                groupId = null;
            }
            else
            {
                var grp = await dashboards.FindGroupBySlugAsync(ws.Id, request.GroupSlug);
                if (grp is null)
                    return Problems.NotFound($"Dashboard group '{request.GroupSlug}' not found.");
                groupId = grp.Id;
            }
        }

        var updated = existing with
        {
            GroupId = groupId,
            Name = request.Name ?? existing.Name,
            Icon = request.Icon ?? existing.Icon,
            LayoutJson = request.LayoutJson ?? existing.LayoutJson,
            WidgetsJson = request.WidgetsJson ?? existing.WidgetsJson,
            Position = request.Position ?? existing.Position,
            UpdatedAt = DateTime.UtcNow,
        };
        await dashboards.UpdateAsync(updated);
        return TypedResults.Ok(new ApiResult<DashboardResult>(ToResult(updated)));
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> Delete(
        string slug,
        string dashSlug,
        IWorkspaceStore workspaces,
        IDashboardStore dashboards
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var existing = await dashboards.FindBySlugAsync(ws.Id, dashSlug);
        if (existing is null)
            return Problems.NotFound($"Dashboard '{dashSlug}' not found.");
        if (existing.IsDefault)
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["dashboard"] = new[]
                    {
                        $"Dashboard '{dashSlug}' is a platform default and cannot be deleted. Edit it instead.",
                    },
                }
            );
        await dashboards.DeleteAsync(ws.Id, dashSlug);
        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    // ============================================================
    // Groups
    // ============================================================

    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<DashboardGroupResult>>>, ProblemHttpResult>
    > ListGroups(string slug, IWorkspaceStore workspaces, IDashboardStore dashboards)
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var groups = await dashboards.ListGroupsAsync(ws.Id);
        return TypedResults.Ok(
            new ApiResult<IReadOnlyList<DashboardGroupResult>>(
                groups.Select(ToGroupResult).ToList()
            )
        );
    }

    private static async Task<
        Results<Ok<ApiResult<DashboardGroupResult>>, ProblemHttpResult>
    > CreateGroup(
        string slug,
        CreateDashboardGroupRequest request,
        IWorkspaceStore workspaces,
        IDashboardStore dashboards,
        HttpContext http
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        if (string.IsNullOrWhiteSpace(request.Slug))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["slug"] = new[] { "slug is required." } }
            );
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["name"] = new[] { "name is required." } }
            );
        if (string.IsNullOrWhiteSpace(request.Icon))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["icon"] = new[] { "icon is required." } }
            );
        if (await dashboards.FindGroupBySlugAsync(ws.Id, request.Slug) is not null)
            return Problems.SlugAlreadyExists(request.Slug);

        var now = DateTime.UtcNow;
        var created = await dashboards.CreateGroupAsync(
            new DashboardGroup(
                Id: Guid.Empty,
                WorkspaceId: ws.Id,
                Slug: request.Slug,
                Name: request.Name,
                Icon: request.Icon,
                Position: request.Position ?? 100,
                IsDefault: false,
                CreatedAt: now,
                UpdatedAt: now,
                CreatedBy: CookieAuthHelpers.GetUserId(http)
            )
        );
        return TypedResults.Ok(new ApiResult<DashboardGroupResult>(ToGroupResult(created)));
    }

    private static async Task<
        Results<Ok<ApiResult<DashboardGroupResult>>, ProblemHttpResult>
    > UpdateGroup(
        string slug,
        string groupSlug,
        UpdateDashboardGroupRequest request,
        IWorkspaceStore workspaces,
        IDashboardStore dashboards
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var existing = await dashboards.FindGroupBySlugAsync(ws.Id, groupSlug);
        if (existing is null)
            return Problems.NotFound($"Dashboard group '{groupSlug}' not found.");
        var updated = existing with
        {
            Name = request.Name ?? existing.Name,
            Icon = request.Icon ?? existing.Icon,
            Position = request.Position ?? existing.Position,
            UpdatedAt = DateTime.UtcNow,
        };
        await dashboards.UpdateGroupAsync(updated);
        return TypedResults.Ok(new ApiResult<DashboardGroupResult>(ToGroupResult(updated)));
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> DeleteGroup(
        string slug,
        string groupSlug,
        IWorkspaceStore workspaces,
        IDashboardStore dashboards
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var existing = await dashboards.FindGroupBySlugAsync(ws.Id, groupSlug);
        if (existing is null)
            return Problems.NotFound($"Dashboard group '{groupSlug}' not found.");
        if (existing.IsDefault)
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["group"] = new[]
                    {
                        $"Group '{groupSlug}' is a platform default and cannot be deleted. Rename or rearrange it instead.",
                    },
                }
            );
        await dashboards.DeleteGroupAsync(ws.Id, groupSlug);
        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    // ============================================================
    // DTOs
    // ============================================================

    public sealed record CreateDashboardRequest(
        string Slug,
        string Name,
        string? Icon,
        string? GroupSlug,
        int? Position
    );

    public sealed record UpdateDashboardRequest(
        string? Name,
        string? Icon,
        string? GroupSlug,
        int? Position,
        string? LayoutJson,
        string? WidgetsJson
    );

    public sealed record CreateDashboardGroupRequest(
        string Slug,
        string Name,
        string Icon,
        int? Position
    );

    public sealed record UpdateDashboardGroupRequest(string? Name, string? Icon, int? Position);

    public sealed record DashboardResult(
        Guid Id,
        Guid WorkspaceId,
        string Slug,
        string Name,
        string? Icon,
        string? GroupSlug,
        string LayoutJson,
        string WidgetsJson,
        int Position,
        bool IsDefault,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    public sealed record DashboardGroupResult(
        Guid Id,
        Guid WorkspaceId,
        string Slug,
        string Name,
        string Icon,
        int Position,
        bool IsDefault,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    private static DashboardResult ToResult(Dashboard d) =>
        new(
            d.Id,
            d.WorkspaceId,
            d.Slug,
            d.Name,
            d.Icon,
            null, // GroupSlug resolved separately when needed; not included in CRUD round-trips.
            d.LayoutJson,
            d.WidgetsJson,
            d.Position,
            d.IsDefault,
            d.CreatedAt,
            d.UpdatedAt
        );

    private static DashboardGroupResult ToGroupResult(DashboardGroup g) =>
        new(
            g.Id,
            g.WorkspaceId,
            g.Slug,
            g.Name,
            g.Icon,
            g.Position,
            g.IsDefault,
            g.CreatedAt,
            g.UpdatedAt
        );

    private static bool IsJson(string? input)
    {
        if (input is null)
            return true;
        try
        {
            using var _ = JsonDocument.Parse(input);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
