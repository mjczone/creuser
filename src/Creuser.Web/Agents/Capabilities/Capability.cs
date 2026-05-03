namespace Creuser.Web.Agents.Capabilities;

/// <summary>
/// One discoverable thing the platform can do — an admin action, a settings
/// surface, an operator workflow. Used by the in-app AI assistant's
/// <c>describe_capabilities</c> + <c>navigate</c> tools to answer "where do
/// I do X?" / "what can I do here?".
///
/// Designed as a value record so providers can hand-curate them inline OR
/// derive them from <c>[AiCapability]</c> attributes on endpoints once that
/// reflection-based scanner lands. Same shape regardless of source.
/// </summary>
public sealed record Capability(
    /// <summary>Stable identifier — e.g. <c>users.create</c>, <c>environment.anthropic</c>.</summary>
    string Id,
    /// <summary>Human-readable section the capability belongs to — e.g. <c>users</c>, <c>branding</c>, <c>environment</c>.</summary>
    string Topic,
    /// <summary>Short label for the capability — used in navigation links.</summary>
    string Title,
    /// <summary>One-or-two-sentence description focused on <em>when</em> an admin would want this. Drives AI tool selection quality.</summary>
    string Description,
    /// <summary>Free-text intents this capability matches — phrases an operator might type. The navigate tool scores against these.</summary>
    IReadOnlyList<string> Intents,
    /// <summary>SPA route to send the user to. Null when the capability is purely informational.</summary>
    string? Route = null,
    /// <summary>Optional section key (e.g. <c>aiAnthropic</c>) the SPA should auto-expand on arrival via the <c>?expand=</c> query param.</summary>
    string? ExpandSection = null,
    /// <summary>Required role to even see this capability. <c>Admin</c> capabilities never get returned to non-admin users — protects against the AI suggesting actions the user can't perform.</summary>
    string RequiresRole = "User",
    /// <summary>Whether this capability mutates state. Read-only capabilities are safe to suggest freely; mutating ones get a confirmation step in future tool versions.</summary>
    bool Mutates = false
);

/// <summary>
/// Per-call context handed to capability providers so they can return
/// dynamic / scoped capabilities — workspace-specific actions, plugin-
/// contributed surfaces, role-aware subsets. The static
/// <see cref="CoreCapabilityProvider"/> mostly ignores this; future
/// per-workspace providers will read it.
/// </summary>
public sealed record CapabilityContext(
    string Role,
    string? CurrentScreen = null,
    Guid? WorkspaceId = null
);
