namespace Creuser.Core.Repositories;

/// <summary>
/// One saved dashboard — a serialized dockview-vue layout plus the list of
/// widget instances its panels reference. Identified within a workspace by
/// <see cref="Slug"/>; routed at <c>/w/:slug/d/:dashboardSlug</c>.
/// <see cref="GroupId"/> is null when the dashboard is standalone (its own
/// icon-bar entry); set when it lives inside a sidebar group.
/// </summary>
public sealed record Dashboard(
    Guid Id,
    Guid WorkspaceId,
    Guid? GroupId,
    string Slug,
    string Name,
    string? Icon,
    /// <summary>Serialized dockview-vue <c>SerializedDockview</c> JSON. Empty object on first save.</summary>
    string LayoutJson,
    /// <summary>JSON array of <c>{ id, widgetType, props }</c> objects. Layout panels reference these by id.</summary>
    string WidgetsJson,
    int Position,
    /// <summary>True when shipped by the seeder; protects from hard-delete.</summary>
    bool IsDefault,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy
);

/// <summary>
/// UI grouping for dashboards. A group is one icon in the workspace icon
/// bar; clicking it opens the sub-sidebar listing the group's children.
/// Standalone dashboards skip the group and have their own icon directly.
/// </summary>
public sealed record DashboardGroup(
    Guid Id,
    Guid WorkspaceId,
    string Slug,
    string Name,
    string Icon,
    int Position,
    bool IsDefault,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy
);

/// <summary>
/// Pre-flattened nav-tree for the workspace icon bar. Returned by the
/// <c>/api/workspaces/{slug}/dashboards/</c> endpoint so the SPA can render
/// the icon bar + sub-sidebar from one round trip.
/// </summary>
public sealed record DashboardNavTree(
    IReadOnlyList<DashboardNavGroup> Groups,
    IReadOnlyList<DashboardNavItem> Standalones
);

public sealed record DashboardNavGroup(
    string Slug,
    string Name,
    string Icon,
    int Position,
    IReadOnlyList<DashboardNavItem> Children,
    /// <summary>True for groups the platform seeds during workspace creation. Settings UIs use this to gate destructive actions (delete refused server-side; SPA disables the button).</summary>
    bool IsDefault = false
);

public sealed record DashboardNavItem(
    string Slug,
    string Name,
    string? Icon,
    int Position,
    /// <summary>True for the platform's seeded "Home" dashboard (and any future seeded dashboards). Settings UIs use this to gate destructive actions.</summary>
    bool IsDefault = false
);
