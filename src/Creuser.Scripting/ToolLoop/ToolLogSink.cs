namespace Creuser.Scripting.ToolLoop;

/// <summary>
/// Closure-captured sink the registry's tool wrappers call once per
/// invocation so the runner can build the <c>tool_log.json</c> sidecar
/// without the registry having to know runner internals.
///
/// <para>
/// The runner constructs one sink per loop, hands it to
/// <see cref="IToolLoopToolRegistry.BuildTools"/>, and the registry's
/// <see cref="Microsoft.Extensions.AI.AIFunction"/> wrappers call
/// <see cref="Record"/> immediately after each tool body executes.
/// </para>
/// </summary>
public sealed class ToolLogSink
{
    private readonly object _lock = new();
    private readonly List<ToolLogEntry> _entries = new();

    /// <summary>
    /// Loop turn the runner is currently executing. Mutated by the runner
    /// before each model call and read by tools when they record. Lets the
    /// registry stay agnostic of loop state — tools just stamp whatever the
    /// runner says is "now".
    /// </summary>
    public int CurrentTurn { get; set; }

    /// <summary>
    /// Set once per loop and consulted by the runner after each tool's
    /// callback returns. When true, the runner aborts the loop with
    /// <c>termination_reason: "tool_error_unrecoverable"</c>.
    /// </summary>
    public bool FatalEncountered { get; private set; }
    public string? FatalReason { get; private set; }

    public IReadOnlyList<ToolLogEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToArray();
        }
    }

    /// <summary>
    /// Append one tool-invocation record. Thread-safe — tools may execute
    /// concurrently if a future model emits parallel calls.
    /// </summary>
    public void Record(ToolLogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (entry.Fatal && !FatalEncountered)
            {
                FatalEncountered = true;
                FatalReason = entry.Error ?? $"Tool '{entry.Tool}' raised an unrecoverable error.";
            }
        }
    }
}

/// <summary>
/// One per-invocation audit record. Emitted to <c>tool_log.json</c>; the
/// run-detail UI scans this when an operator asks "what did this loop
/// touch?" — easier than walking the full transcript.
/// </summary>
public sealed record ToolLogEntry(
    /// <summary>Loop turn (0-indexed) the tool ran in. The runner sets this; the registry passes the value through unchanged.</summary>
    int Turn,
    string Tool,
    /// <summary>JSON-serialized arguments the model passed to the tool.</summary>
    string ArgsJson,
    /// <summary>JSON-serialized result the tool returned (or the error envelope on failure).</summary>
    string ResultJson,
    long DurationMs,
    string? Error = null,
    /// <summary>True when the failure must abort the loop — e.g. workspace path-escape attempts. The runner reads this and short-circuits.</summary>
    bool Fatal = false
);
