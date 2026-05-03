using Creuser.Core.Repositories;

namespace Creuser.Core.Projections;

/// <summary>
/// Loads + parses conventions from a workspace's working tree. Walks
/// <c>.creuser/conventions/*.{yaml,yml}</c>, resolves <c>extends:</c>
/// references against the bundled <c>creuser:standard/*</c> set, and
/// returns the merged conventions ready for the scanner.
///
/// <para>
/// Validation errors (malformed YAML, unknown <c>extends:</c>, schema
/// failures) are returned as <see cref="ConventionLoadResult.Errors"/> —
/// callers (the sync service, the validate endpoint) decide whether to
/// abort the run or surface them inline.
/// </para>
/// </summary>
public interface IConventionLoader
{
    Task<ConventionLoadResult> LoadAsync(
        Workspace workspace,
        string workingTreePath,
        CancellationToken ct = default
    );
}

public sealed record ConventionLoadResult(
    IReadOnlyList<Convention> Conventions,
    IReadOnlyList<ConventionLoadError> Errors
);

public sealed record ConventionLoadError(
    /// <summary>Source filename (relative to the workspace root). Null for non-file errors (e.g. config-level).</summary>
    string? Source,
    string Message
);

/// <summary>
/// Orchestrates a full projection rebuild for one workspace. Loads
/// conventions, walks the working tree, materializes entities, resolves
/// refs, replaces the projection in a single transaction, returns the
/// audit report.
/// </summary>
public interface IProjectionSyncService
{
    Task<ProjectionReport> RunAsync(
        Workspace workspace,
        string workingTreePath,
        CancellationToken ct = default
    );
}
