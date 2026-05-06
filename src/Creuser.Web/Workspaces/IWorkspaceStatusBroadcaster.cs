using Creuser.Core.Repositories;
using Creuser.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Creuser.Web.Workspaces;

/// <summary>
/// Publishes workspace status updates to the SPA over SignalR. Endpoints
/// call this after every successful state-mutating verb
/// (<c>WriteAsync</c> / <c>CommitAsync</c> / <c>PushAsync</c> /
/// <c>SyncAsync</c>) so the header's Commit/Push buttons reflect fresh
/// counts in real time. The SPA subscribes to the per-workspace channel
/// <c>workspace:&lt;slug&gt;:status</c> via the existing
/// <see cref="NotificationsHub"/>.
///
/// <para>
/// Wraps <see cref="IHubContext{T}"/> so the rest of the codebase
/// doesn't import SignalR types — keeps the dependency graph honest and
/// makes future channel formats easy to evolve in one place.
/// </para>
/// </summary>
public interface IWorkspaceStatusBroadcaster
{
    Task BroadcastAsync(
        string slug,
        WorkspaceProviderStatus status,
        CancellationToken ct = default
    );
}

public sealed class WorkspaceStatusBroadcaster : IWorkspaceStatusBroadcaster
{
    private readonly IHubContext<NotificationsHub> _hub;

    public WorkspaceStatusBroadcaster(IHubContext<NotificationsHub> hub)
    {
        _hub = hub;
    }

    public Task BroadcastAsync(
        string slug,
        WorkspaceProviderStatus status,
        CancellationToken ct = default
    )
    {
        var channel = $"workspace:{slug}:status";
        // The hub's protocol is `notification(channel, payload)` — same
        // shape as `NotificationsHub.Broadcast(channel, payload)` exposes
        // for hub-to-hub calls. Sending direct via `IHubContext` skips
        // the round-trip but uses the same group + event name so the
        // SPA's listener doesn't care which path published.
        return _hub.Clients.Group(channel).SendAsync("notification", channel, status, ct);
    }
}
