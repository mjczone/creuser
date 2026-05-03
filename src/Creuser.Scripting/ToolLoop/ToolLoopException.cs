namespace Creuser.Scripting.ToolLoop;

/// <summary>
/// Thrown by <see cref="IToolLoopToolRegistry"/> implementations when a
/// caller asks for a tool the registry doesn't know about. Surfaces back
/// to <see cref="LlmToolLoopStepRunner"/> as a step-entry validation
/// failure — the loop never starts.
/// </summary>
public sealed class ToolLoopException : Exception
{
    public ToolLoopException(string message)
        : base(message) { }

    public ToolLoopException(string message, Exception inner)
        : base(message, inner) { }
}
