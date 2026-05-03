using Creuser.Auth.Abstractions;

namespace Creuser.Web.Agents.Capabilities;

/// <summary>
/// Declares a <see cref="Capability"/> on the endpoint method (or any
/// reflected method) that surfaces / backs the action. The startup
/// <see cref="EndpointAttributeProvider"/> scanner reflects over the
/// assembly and emits a <see cref="Capability"/> per attribute, replacing
/// the corresponding hand-written entry in <see cref="CoreCapabilityProvider"/>.
///
/// <para>
/// One method may carry multiple <c>[AiCapability]</c> attributes — useful
/// when a single GET endpoint anchors several distinct admin actions on the
/// same SPA surface (e.g. branding theme + branding preset both live on
/// <c>/settings/branding</c> and use the same backing config endpoint).
/// </para>
///
/// <para>
/// The intent vocabulary is the load-bearing field for AI tool selection
/// quality. Add the phrases an admin would type in chat. Phrases like
/// "change colors" or "configure smtp" are good; a single bland word is
/// usually too coarse to score well.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class AiCapabilityAttribute : Attribute
{
    /// <summary>Stable identifier — e.g. <c>users.create</c>, <c>environment.anthropic</c>.</summary>
    public string Id { get; }

    /// <summary>Topic this capability belongs to — e.g. <c>users</c>, <c>branding</c>.</summary>
    public string Topic { get; }

    /// <summary>Short label shown in navigation links.</summary>
    public string Title { get; }

    /// <summary>One-or-two-sentence description focused on <em>when</em> an admin would use this.</summary>
    public string Description { get; }

    /// <summary>Intent phrases an operator might type. Drives navigate-tool scoring.</summary>
    public string[] Intents { get; }

    /// <summary>SPA route to navigate to. Null when the capability is purely informational.</summary>
    public string? Route { get; init; }

    /// <summary>Optional section key for <c>?expand=</c> deep-open semantics.</summary>
    public string? ExpandSection { get; init; }

    /// <summary>Role required to see this capability. Defaults to <see cref="Roles.User"/>.</summary>
    public string RequiresRole { get; init; } = Roles.User;

    /// <summary>Whether invoking this capability mutates state.</summary>
    public bool Mutates { get; init; }

    public AiCapabilityAttribute(
        string id,
        string topic,
        string title,
        string description,
        params string[] intents
    )
    {
        Id = id;
        Topic = topic;
        Title = title;
        Description = description;
        Intents = intents ?? Array.Empty<string>();
    }

    internal Capability ToCapability() =>
        new(
            Id: Id,
            Topic: Topic,
            Title: Title,
            Description: Description,
            Intents: Intents,
            Route: Route,
            ExpandSection: ExpandSection,
            RequiresRole: RequiresRole,
            Mutates: Mutates
        );
}
