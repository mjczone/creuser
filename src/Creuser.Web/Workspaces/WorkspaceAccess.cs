using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Core.Repositories;

namespace Creuser.Web.Workspaces;

/// <summary>
/// Per-request workspace access resolver. Returns the matched
/// <see cref="Workspace"/> + the caller's effective role
/// (<c>Admin</c> | <c>Editor</c> | <c>Viewer</c>) when access is allowed,
/// <c>null</c> when the workspace doesn't exist OR the caller has no
/// access. Admin-ness implies <see cref="WorkspaceRole.Editor"/>; other
/// users need an explicit <c>cr.workspace_members</c> row.
///
/// <para>
/// Endpoints call <see cref="RequireAccessAsync"/> for read-side gating
/// and <see cref="RequireEditorAsync"/> for mutations. The helper
/// collapses the existence check + access check into one round-trip and
/// returns <c>null</c> uniformly so endpoints can answer 404 in both
/// cases — exposing membership existence to non-admins is the
/// architecture's stance ("not exposing existence to non-members").
/// </para>
/// </summary>
public static class WorkspaceAccess
{
    /// <summary>
    /// Resolve the caller's access to a workspace. Returns null when the
    /// workspace doesn't exist OR the caller has no membership and isn't
    /// an admin.
    /// </summary>
    public static async Task<WorkspaceAccessResult?> RequireAccessAsync(
        HttpContext http,
        string slug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        CancellationToken ct = default
    )
    {
        var ws = await workspaces.FindBySlugAsync(slug, ct);
        if (ws is null)
            return null;
        var userId = CookieAuthHelpers.GetUserId(http);
        if (userId is null)
            return null;
        var isAdmin = http.User.IsInRole(Roles.Admin);
        var role = await members.GetRoleAsync(ws.Id, userId.Value, isAdmin, ct);
        if (role is null)
            return null;
        return new WorkspaceAccessResult(ws, role, isAdmin);
    }

    /// <summary>
    /// Same as <see cref="RequireAccessAsync"/> but returns null when
    /// the caller is a Viewer — i.e. requires Admin or Editor access.
    /// Used by mutation endpoints.
    /// </summary>
    public static async Task<WorkspaceAccessResult?> RequireEditorAsync(
        HttpContext http,
        string slug,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        CancellationToken ct = default
    )
    {
        var access = await RequireAccessAsync(http, slug, workspaces, members, ct);
        if (access is null)
            return null;
        if (access.Role == WorkspaceRole.Viewer)
            return null;
        return access;
    }

    /// <summary>
    /// Resolve the workspaces a caller can access. Admins see all; users
    /// see only workspaces where they have a <c>cr.workspace_members</c>
    /// row. Returns the workspaces sorted in the underlying store's
    /// list order.
    /// </summary>
    public static async Task<IReadOnlyList<Workspace>> ListAccessibleAsync(
        HttpContext http,
        IWorkspaceStore workspaces,
        IWorkspaceMemberStore members,
        int skip,
        int take,
        CancellationToken ct = default
    )
    {
        var isAdmin = http.User.IsInRole(Roles.Admin);
        if (isAdmin)
        {
            return await workspaces.ListAsync(skip, take, ct);
        }
        var userId = CookieAuthHelpers.GetUserId(http);
        if (userId is null)
            return Array.Empty<Workspace>();
        var ids = await members.ListWorkspaceIdsForUserAsync(userId.Value, ct);
        if (ids.Count == 0)
            return Array.Empty<Workspace>();
        var idSet = ids.ToHashSet();
        // Pull the full set then filter — `IWorkspaceStore.ListAsync` doesn't
        // expose a "list by ids" overload yet. v0.2 might add one if the
        // membership table grows large; today the typical workspace set is
        // small enough that this is cheaper than per-id round-trips.
        var all = await workspaces.ListAsync(0, int.MaxValue, ct);
        return all.Where(w => idSet.Contains(w.Id)).Skip(skip).Take(take).ToList();
    }
}

public sealed record WorkspaceAccessResult(Workspace Workspace, string Role, bool IsAdmin);
