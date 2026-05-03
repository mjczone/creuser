namespace Creuser.Core.Execution;

/// <summary>
/// One executable tool the platform's shell / script runners are aware of.
/// Surfaced through <c>GET /api/tools</c> so the Jobs editor can offer a
/// picker instead of asking operators to memorize the binaries baked into
/// the deployment image.
/// </summary>
public sealed record ToolEntry(
    /// <summary>Binary name as it would appear on PATH (and in the job's `allowed_commands` list).</summary>
    string Name,
    /// <summary>Human-readable category for grouping in the picker.</summary>
    string Category,
    string? Description,
    /// <summary>One of <c>baseline</c> (curated palette in the image), <c>plugin:&lt;id&gt;</c> (contributed by a loaded plugin), <c>system</c> (always present).</summary>
    string Source
);

/// <summary>
/// Composes <see cref="ToolEntry"/> contributions from the host (the
/// curated baseline palette) and from loaded plugins. The Jobs editor calls
/// <see cref="List"/> at dialog-open to populate the picker; the contract
/// is intentionally synchronous + cheap (no I/O) — it returns whatever the
/// host knows about, regardless of whether the binaries are actually
/// resolvable on PATH right now.
///
/// <para>
/// "Available on PATH right now" is a separate concern handled by a future
/// <c>IToolProber</c> abstraction (Probe-on-startup or probe-on-list). For
/// v0.1 the catalog reflects the deployment's <em>declared</em> palette,
/// not the runtime-resolvable set. This is correct: the allow-list should
/// be authored against the image's curated palette, and missing-binary
/// errors at run time surface clearly via the shell runner.
/// </para>
/// </summary>
public interface IToolCatalog
{
    IReadOnlyList<ToolEntry> List();
}
