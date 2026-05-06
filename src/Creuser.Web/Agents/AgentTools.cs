using System.ComponentModel;
using System.Text.RegularExpressions;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Scripting.ToolLoop;
using Creuser.Web.Agents.Capabilities;
using Microsoft.Extensions.AI;

namespace Creuser.Web.Agents;

/// <summary>
/// Builds the M.E.AI <see cref="AIFunction"/> instances passed to the
/// in-app assistant. The chat now bridges three tool layers:
///
/// <list type="bullet">
///   <item>
///     <strong>Navigation</strong> — <c>navigate(intent)</c> +
///     <c>describe_capabilities(topic?)</c>. Match user intent to an
///     <see cref="Capability"/> route or list available capabilities.
///   </item>
///   <item>
///     <strong>Content data</strong> — every tool exposed by every
///     <see cref="IToolLoopToolRegistry"/> registered in DI. Today that
///     includes <see cref="WorkspaceToolLoopRegistry"/> (file system + git
///     reads) and <see cref="ProjectionToolLoopRegistry"/> (entity graph
///     queries: <c>list_kinds</c>, <c>query_entities</c>, <c>get_entity</c>,
///     <c>find_references</c>, <c>find_orphans</c>, <c>find_unresolved_refs</c>).
///     Plugins extend this surface — anything they register on
///     <c>IToolLoopToolRegistry</c> auto-appears here.
///   </item>
///   <item>
///     <strong>Actions</strong> — same registries' mutating tools (when
///     they have any). The chat treats them the same as the job runner
///     does; the registry decides whether a tool reads or writes.
///   </item>
/// </list>
///
/// Workspace-scoped tools require a <see cref="StepContext"/>. We
/// synthesize one per chat turn from the user's current SPA route — when
/// they're on <c>/w/&lt;slug&gt;/...</c> we resolve the workspace, build
/// the working-tree path, and pass that into every registry's
/// <see cref="IToolLoopToolRegistry.BuildTools"/>. Off a workspace
/// route, only the navigation tools are exposed.
/// </summary>
public sealed class AgentTools
{
    private static readonly Regex WorkspaceRouteRegex = new(
        @"^/w/(?<slug>[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?)(/|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private readonly CapabilityRegistry _registry;
    private readonly IEnumerable<IToolLoopToolRegistry> _toolRegistries;
    private readonly IWorkspaceStore _workspaces;
    private readonly IWorkspaceWorkingTree _workingTree;
    private readonly ILoggerFactory _loggerFactory;

    public AgentTools(
        CapabilityRegistry registry,
        IEnumerable<IToolLoopToolRegistry> toolRegistries,
        IWorkspaceStore workspaces,
        IWorkspaceWorkingTree workingTree,
        ILoggerFactory loggerFactory
    )
    {
        _registry = registry;
        _toolRegistries = toolRegistries;
        _workspaces = workspaces;
        _workingTree = workingTree;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Build the per-request tool list. Captures the calling user's
    /// <see cref="CapabilityContext"/> so each tool invocation knows who's
    /// asking and filters results accordingly.
    /// </summary>
    public async Task<IList<AITool>> BuildToolsForContextAsync(
        CapabilityContext ctx,
        CancellationToken ct = default
    )
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

        var tools = new List<AITool> { navigate, describe };

        // Bridge: every workspace-scoped tool registry's full surface
        // becomes part of the chat's tool list when the user is on a
        // /w/<slug>/... route. Off a workspace route there's no working
        // tree to operate against, so the chat only gets navigation +
        // describe_capabilities — explaining the limitation in the
        // system prompt would be the right way to surface this.
        var workspaceSlug = ParseWorkspaceSlug(ctx.CurrentScreen);
        if (!string.IsNullOrEmpty(workspaceSlug))
        {
            try
            {
                var workspace = await _workspaces.FindBySlugAsync(workspaceSlug, ct);
                if (workspace is not null)
                {
                    var workingTreePath =
                        await _workingTree.ResolvePathAsync(workspace, ct) ?? string.Empty;
                    var stepCtx = new StepContext(
                        RunId: Guid.NewGuid(),
                        WorkspaceId: workspace.Id,
                        WorkspaceSlug: workspace.Slug,
                        WorkingTreePath: workingTreePath,
                        StepId: Guid.NewGuid(),
                        StepName: "chat",
                        Budgets: new StepBudgets(),
                        Logger: _loggerFactory.CreateLogger("Creuser.Web.Agents.Chat")
                    );
                    var sink = new ToolLogSink();

                    foreach (var toolRegistry in _toolRegistries)
                    {
                        var names = toolRegistry.AvailableTools;
                        if (names.Count == 0)
                            continue;
                        try
                        {
                            var built = toolRegistry.BuildTools(names, stepCtx, sink);
                            foreach (var fn in built)
                                tools.Add(fn);
                        }
                        catch (ToolLoopException ex)
                        {
                            // One registry failing to materialize shouldn't
                            // kill the whole chat surface — log and skip.
                            _loggerFactory
                                .CreateLogger<AgentTools>()
                                .LogWarning(
                                    ex,
                                    "Skipping tool registry {Registry} for chat — BuildTools threw.",
                                    toolRegistry.GetType().Name
                                );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Workspace lookup or working-tree resolution failed; log
                // and fall through to the navigation-only chat.
                _loggerFactory
                    .CreateLogger<AgentTools>()
                    .LogWarning(
                        ex,
                        "Failed to resolve workspace context for chat (slug={Slug}); chat will run with navigation tools only.",
                        workspaceSlug
                    );
            }
        }

        return tools;
    }

    /// <summary>
    /// Pull the workspace slug from a SPA route like
    /// <c>/w/foo/settings/conventions</c>. Returns <c>null</c> when the
    /// route isn't workspace-scoped (e.g. <c>/settings/branding</c>,
    /// <c>/login</c>).
    /// </summary>
    private static string? ParseWorkspaceSlug(string? currentScreen)
    {
        if (string.IsNullOrWhiteSpace(currentScreen))
            return null;
        var match = WorkspaceRouteRegex.Match(currentScreen);
        return match.Success ? match.Groups["slug"].Value : null;
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
