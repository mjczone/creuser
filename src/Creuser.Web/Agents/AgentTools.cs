using System.ComponentModel;
using Creuser.Web.Agents.Capabilities;
using Microsoft.Extensions.AI;

namespace Creuser.Web.Agents;

/// <summary>
/// Builds the M.E.AI <see cref="AIFunction"/> instances passed to the
/// in-app assistant. v1 ships two tools — both read-only, both heavily
/// scoped:
///
/// <list type="bullet">
///   <item>
///     <c>navigate(intent)</c> — match a free-text user intent to a single
///     <see cref="Capability"/> in the registry. Returns title, description,
///     route, and the section key the SPA should auto-expand on arrival.
///   </item>
///   <item>
///     <c>describe_capabilities(topic?)</c> — list capabilities, optionally
///     filtered to a topic (<c>users</c>, <c>branding</c>, etc.). Lets the
///     LLM answer "what can I do?" without inventing things.
///   </item>
/// </list>
///
/// Both tools resolve via <see cref="CapabilityRegistry"/>, which already
/// filters by user role. The LLM never receives entries the calling user
/// can't act on, so it can't suggest "go to /settings/users" to a non-admin.
///
/// Mutating tools (<c>call_api</c>, etc.) are deliberately not in this v1
/// — they require a UI confirmation step before firing, which is the next
/// pass of work.
/// </summary>
public sealed class AgentTools
{
    private readonly CapabilityRegistry _registry;

    public AgentTools(CapabilityRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Build the per-request tool list. Captures the calling user's
    /// <see cref="CapabilityContext"/> so each tool invocation knows who's
    /// asking and filters results accordingly.
    /// </summary>
    public IList<AITool> BuildToolsForContext(CapabilityContext ctx)
    {
        var registry = _registry;

        var navigate = AIFunctionFactory.Create(
            async (
                [Description(
                    "Free-text description of what the user wants to do or where they want to go. "
                        + "E.g. 'set the anthropic key', 'invite a user', 'change the theme'."
                )]
                    string intent,
                CancellationToken ct
            ) =>
            {
                var caps = await registry.GetAvailableAsync(ctx, ct);
                var match = ScoreAndPick(caps, intent);
                if (match is null)
                {
                    return new
                    {
                        matched = false,
                        message = "No matching capability found in the user's allowed surface. "
                            + "Tell the user this isn't something they can currently do, or that they may need to ask an admin.",
                    };
                }
                return (object)
                    new
                    {
                        matched = true,
                        match.Id,
                        match.Title,
                        match.Description,
                        match.Route,
                        match.ExpandSection,
                        match.RequiresRole,
                        match.Mutates,
                        instructions = match.Route is not null
                            ? "Render a markdown link in your reply that the SPA can render as a clickable button — `["
                                + match.Title
                                + "]("
                                + BuildLinkUrl(match.Route, match.ExpandSection)
                                + ")`."
                            : "Tell the user how to do this without a route — there's no specific page for it.",
                    };
            },
            name: "navigate",
            description: "Match a user's intent to a single Creuser capability. "
                + "Returns the title, description, route, and an instruction for how to render a clickable link in the reply. "
                + "Use this whenever the user is asking 'where do I X' / 'how do I X'."
        );

        var describe = AIFunctionFactory.Create(
            async (
                [Description(
                    "Optional topic to filter by — e.g. 'users', 'branding', 'environment', 'account'. "
                        + "Omit to see everything available."
                )]
                    string? topic,
                CancellationToken ct
            ) =>
            {
                var caps = await registry.GetAvailableAsync(ctx, ct);
                if (!string.IsNullOrWhiteSpace(topic))
                {
                    var t = topic.ToLowerInvariant();
                    caps =
                    [
                        .. caps.Where(c => c.Topic.Equals(t, StringComparison.OrdinalIgnoreCase)),
                    ];
                }
                return caps.Select(c => new
                    {
                        c.Id,
                        c.Topic,
                        c.Title,
                        c.Description,
                        c.Route,
                        c.ExpandSection,
                        c.RequiresRole,
                        c.Mutates,
                    })
                    .ToList();
            },
            name: "describe_capabilities",
            description: "List the capabilities currently available to the user, optionally filtered by topic. "
                + "Use this when the user is asking what the platform can do, what features are available, "
                + "or browsing for ideas. Don't list every capability in your reply — summarize and offer to drill in."
        );

        return new List<AITool> { navigate, describe };
    }

    private static string BuildLinkUrl(string route, string? expandSection)
    {
        return string.IsNullOrWhiteSpace(expandSection)
            ? route
            : $"{route}?expand={Uri.EscapeDataString(expandSection)}";
    }

    /// <summary>
    /// Simple keyword + intent-phrase scoring. Good enough for v1; replaces
    /// with a vectorized match (or pure LLM-driven retrieval over
    /// <c>describe_capabilities</c>) when the catalog grows past ~50 entries
    /// and selection quality starts dropping.
    /// </summary>
    private static Capability? ScoreAndPick(IReadOnlyList<Capability> caps, string intent)
    {
        var query = intent.ToLowerInvariant();
        var queryWords = query.Split(
            [' ', ',', '.', ';', '?', '!'],
            StringSplitOptions.RemoveEmptyEntries
        );

        Capability? best = null;
        var bestScore = 0;

        foreach (var cap in caps)
        {
            var score = 0;

            foreach (var phrase in cap.Intents)
            {
                var p = phrase.ToLowerInvariant();
                if (query.Contains(p))
                    score += p.Length; // longer phrase match = stronger signal
            }

            foreach (var word in queryWords)
            {
                if (cap.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
                    score += 3;
                if (cap.Description.Contains(word, StringComparison.OrdinalIgnoreCase))
                    score += 1;
                if (cap.Topic.Contains(word, StringComparison.OrdinalIgnoreCase))
                    score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = cap;
            }
        }

        return best;
    }
}
