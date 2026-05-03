using Creuser.Auth.Abstractions;

namespace Creuser.Web.Agents.Capabilities;

/// <summary>
/// Aggregates all registered <see cref="ICapabilityProvider"/> instances
/// and applies role-visibility filtering. The AI tool registry calls this
/// to get the slice of capabilities the calling user is allowed to see —
/// the LLM never receives entries it couldn't act on, so it doesn't waste
/// turns suggesting them.
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly IEnumerable<ICapabilityProvider> _providers;

    public CapabilityRegistry(IEnumerable<ICapabilityProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// Compose all providers' capabilities, then filter by role. <c>Admin</c>
    /// sees everything; <c>User</c> only sees <c>RequiresRole = User</c>
    /// entries. Future: workspace-scoped filtering once workspaces are wired.
    /// </summary>
    public async Task<IReadOnlyList<Capability>> GetAvailableAsync(
        CapabilityContext ctx,
        CancellationToken ct = default
    )
    {
        var all = new List<Capability>();
        foreach (var p in _providers)
            all.AddRange(await p.GetAsync(ctx, ct));

        // Dedupe by Id (last write wins). Lets endpoint-anchored
        // [AiCapability] attributes supersede stage-1 hand-written entries
        // during the migration without duplicating in the AI's tool view.
        // Also defends against accidental double-publication when two
        // providers contribute overlapping Ids.
        var byId = new Dictionary<string, Capability>(StringComparer.Ordinal);
        foreach (var c in all)
            byId[c.Id] = c;

        return byId.Values.Where(c => RoleAllows(ctx.Role, c.RequiresRole)).ToList();
    }

    private static bool RoleAllows(string userRole, string requiredRole)
    {
        // Simple ladder for v1: Admin ≥ User. Promotes to a real permission
        // matrix when workspace-scoped roles (Editor / Viewer) land.
        if (requiredRole == Roles.User)
            return true;
        return userRole == Roles.Admin;
    }
}
