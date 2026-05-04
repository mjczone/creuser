using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Repositories;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

/// <summary>
/// CRUD for workspace memberships. Admin-gated in v1 — operators can
/// always reach members through the platform admin endpoints
/// (<c>/api/admin/users</c>) so we keep the per-workspace surface simple.
/// v0.2 may relax read access to workspace-viewers when the assistant
/// learns to answer "who has access to this workspace?".
///
/// <para>
/// Membership is the only access-control axis for non-admin users —
/// a user with no <c>cr.workspace_members</c> rows for a workspace
/// cannot see it (admins are implicit; their access is calculated, not
/// stored). All endpoints here mutate explicit grants only.
/// </para>
/// </summary>
public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/members")
            .WithTags("Members")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", (Delegate)List).WithName("ListWorkspaceMembers");
        group.MapPost("/", (Delegate)Add).WithName("AddWorkspaceMember");
        group.MapPut("/{userId:guid}", (Delegate)UpdateRole).WithName("UpdateWorkspaceMember");
        group.MapDelete("/{userId:guid}", (Delegate)Remove).WithName("RemoveWorkspaceMember");

        return app;
    }

    [AiCapability(
        "members.list",
        "members",
        "Workspace members",
        "Browse the users who have explicit access to this workspace, with their per-workspace role (Editor or Viewer). Admins always have implicit Editor access on every workspace and don't appear here unless they were also granted explicit rows. Used by the workspace's Members dashboard widget and the Settings → Members page.",
        "list members",
        "show members",
        "who has access",
        "workspace members",
        Route = "/w/:slug/settings/members",
        RequiresRole = Roles.Admin
    )]
    private static async Task<
        Results<Ok<ApiResult<IReadOnlyList<MemberResult>>>, ProblemHttpResult>
    > List(
        string slug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        var rows = await members.ListByWorkspaceAsync(ws.Id, ct);
        IReadOnlyList<MemberResult> result = rows.Select(ToResult).ToList();
        return TypedResults.Ok(new ApiResult<IReadOnlyList<MemberResult>>(result));
    }

    [AiCapability(
        "members.add",
        "members",
        "Add a member to a workspace",
        "Grant a user Editor or Viewer access to this workspace. Admin-only. The user must already exist in the platform's user store; this endpoint does not invite or create users — invite-by-email lands when SMTP wiring ships.",
        "add member",
        "grant access",
        "invite to workspace",
        Route = "/w/:slug/settings/members",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<MemberResult>>, ProblemHttpResult>> Add(
        string slug,
        AddMemberRequest request,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IUserStore users,
        HttpContext http,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        if (!WorkspaceRole.IsValid(request.Role))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["role"] = new[] { "Role must be 'Editor' or 'Viewer'." },
                }
            );
        var user = await users.FindByIdAsync(request.UserId);
        if (user is null)
            return Problems.UserNotFound(request.UserId);
        var saved = await members.AddOrUpdateAsync(
            new WorkspaceMember(
                WorkspaceId: ws.Id,
                UserId: request.UserId,
                Role: request.Role,
                GrantedAt: DateTime.UtcNow,
                GrantedBy: CookieAuthHelpers.GetUserId(http)
            ),
            ct
        );
        return TypedResults.Ok(
            new ApiResult<MemberResult>(
                new MemberResult(
                    UserId: saved.UserId,
                    Email: user.Email,
                    DisplayName: user.DisplayName,
                    Role: saved.Role,
                    GrantedAt: saved.GrantedAt,
                    GrantedBy: saved.GrantedBy,
                    IsActive: user.IsActive
                )
            )
        );
    }

    [AiCapability(
        "members.update",
        "members",
        "Change a member's role",
        "Change one workspace member's role between Editor and Viewer. Admin-only.",
        "change role",
        "make editor",
        "make viewer",
        Route = "/w/:slug/settings/members",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<MemberResult>>, ProblemHttpResult>> UpdateRole(
        string slug,
        Guid userId,
        UpdateMemberRequest request,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        IUserStore users,
        HttpContext http,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        if (!WorkspaceRole.IsValid(request.Role))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["role"] = new[] { "Role must be 'Editor' or 'Viewer'." },
                }
            );
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return Problems.UserNotFound(userId);
        var saved = await members.AddOrUpdateAsync(
            new WorkspaceMember(
                WorkspaceId: ws.Id,
                UserId: userId,
                Role: request.Role,
                GrantedAt: DateTime.UtcNow,
                GrantedBy: CookieAuthHelpers.GetUserId(http)
            ),
            ct
        );
        return TypedResults.Ok(
            new ApiResult<MemberResult>(
                new MemberResult(
                    UserId: saved.UserId,
                    Email: user.Email,
                    DisplayName: user.DisplayName,
                    Role: saved.Role,
                    GrantedAt: saved.GrantedAt,
                    GrantedBy: saved.GrantedBy,
                    IsActive: user.IsActive
                )
            )
        );
    }

    [AiCapability(
        "members.remove",
        "members",
        "Remove a member from a workspace",
        "Revoke a user's explicit access to this workspace. Admin-only. Removing the row only revokes Editor/Viewer access; admins keep their implicit access regardless.",
        "remove member",
        "revoke access",
        Route = "/w/:slug/settings/members",
        RequiresRole = Roles.Admin,
        Mutates = true
    )]
    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> Remove(
        string slug,
        Guid userId,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        CancellationToken ct
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug);
        if (ws is null)
            return Problems.WorkspaceNotFound(slug);
        await members.RemoveAsync(ws.Id, userId, ct);
        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    private static MemberResult ToResult(WorkspaceMemberWithUser m) =>
        new(
            UserId: m.UserId,
            Email: m.Email,
            DisplayName: m.DisplayName,
            Role: m.Role,
            GrantedAt: m.GrantedAt,
            GrantedBy: m.GrantedBy,
            IsActive: m.IsActive
        );

    public sealed record AddMemberRequest(Guid UserId, string Role);

    public sealed record UpdateMemberRequest(string Role);

    public sealed record MemberResult(
        Guid UserId,
        string Email,
        string DisplayName,
        string Role,
        DateTime GrantedAt,
        Guid? GrantedBy,
        bool IsActive
    );
}
