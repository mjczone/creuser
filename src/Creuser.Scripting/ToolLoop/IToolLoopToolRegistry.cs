using Creuser.Core.Execution;
using Microsoft.Extensions.AI;

namespace Creuser.Scripting.ToolLoop;

/// <summary>
/// Contributes tools to the agentic <c>llm-tool-loop</c> runner. The host
/// composes every registered <see cref="IToolLoopToolRegistry"/> in DI; the
/// runner asks for the union of <see cref="AvailableTools"/>, validates the
/// frontmatter's tool allow-list against it, and then calls
/// <see cref="BuildTools"/> to materialize the <see cref="AIFunction"/>
/// instances scoped to a specific step.
///
/// <para>
/// v1 ships <see cref="WorkspaceToolLoopRegistry"/> as the default
/// implementation — read-only file system + git tools. Plugins extend the
/// surface by adding their own implementations (DI multi-binding); the
/// runner dedupes by tool name and surfaces conflicts as a step-entry
/// failure rather than silently picking one.
/// </para>
/// </summary>
public interface IToolLoopToolRegistry
{
    /// <summary>
    /// Names of every tool this registry can produce. The frontmatter's
    /// <c>tools:</c> list is validated against the union of this property
    /// across all registered registries before the loop starts. Names
    /// collide across registries are caught by the runner.
    /// </summary>
    IReadOnlyList<string> AvailableTools { get; }

    /// <summary>
    /// Build the M.E.AI <see cref="AIFunction"/> set for the requested
    /// tool names, scoped to a specific step. Implementations capture
    /// <paramref name="ctx"/> and <paramref name="sink"/> in the function
    /// closures so the resulting tools record audit and respect the
    /// working-tree boundary.
    /// </summary>
    /// <exception cref="ToolLoopException">
    /// Thrown if any name in <paramref name="names"/> isn't a tool this
    /// registry knows. Callers (the runner) catch and convert to a step
    /// failure with an operator-readable message.
    /// </exception>
    IReadOnlyList<AIFunction> BuildTools(
        IReadOnlyList<string> names,
        StepContext ctx,
        ToolLogSink sink
    );
}
