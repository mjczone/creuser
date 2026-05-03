using Creuser.Core.Repositories;

namespace Creuser.Core.Execution;

/// <summary>
/// Abstraction over "where on disk does this workspace's content live?"
/// Implementations resolve git workspaces to their cloned working tree
/// (under <c>&lt;dataDir&gt;/workspaces/&lt;slug&gt;/</c>) and local
/// workspaces to the operator-configured path. Step runners that need to
/// read or write workspace content go through <see cref="StepContext.WorkingTreePath"/>,
/// which the executor resolves via this interface.
///
/// <para>
/// Future methods land here as the execution model grows:
/// <c>ApplyAndCommitAsync</c> for the file-mutation transactional commit
/// path, <c>ResolveCommitShaAsync</c> for "what's HEAD right now" queries,
/// etc. Today the interface is intentionally small — it grows alongside
/// the runners that need it.
/// </para>
/// </summary>
public interface IWorkspaceWorkingTree
{
    /// <summary>
    /// Resolve the absolute filesystem path for this workspace's content.
    /// Returns null when the workspace type is unsupported or the path
    /// can't be determined (e.g. local-type workspace with malformed
    /// settings). Callers handle null as "no working tree available."
    /// </summary>
    Task<string?> ResolvePathAsync(Workspace workspace, CancellationToken ct = default);

    /// <summary>
    /// Apply a batch of <see cref="FileChange"/> records to the working
    /// tree, then commit (for git workspaces) and return the resulting
    /// commit SHA. For local workspaces, applies the changes and returns
    /// null (no git history to commit into).
    ///
    /// <para>
    /// This is the architectural seam for "transactional commit per step"
    /// (see architecture.md "File mutation discipline"). The executor calls
    /// this exactly once per step when the step returned non-empty
    /// <see cref="FileChange"/> records. A failure inside this call should
    /// raise — the step's outputs are already persisted, so the executor
    /// records the apply-and-commit error on the step's
    /// <see cref="JobRunStep.ErrorMessage"/> while keeping the outputs
    /// for inspection.
    /// </para>
    /// </summary>
    Task<ApplyAndCommitResult> ApplyAndCommitAsync(
        Workspace workspace,
        string workingTreePath,
        IReadOnlyList<FileChange> changes,
        string commitMessage,
        CancellationToken ct = default
    );

    /// <summary>
    /// Resolve the current HEAD commit SHA of the workspace's working tree.
    /// Returns null for non-git workspaces (or when the working tree has no
    /// commits yet). Used by the executor to record <c>StartCommitSha</c>
    /// on each <see cref="JobRun"/>.
    /// </summary>
    Task<string?> ResolveHeadShaAsync(
        Workspace workspace,
        string workingTreePath,
        CancellationToken ct = default
    );
}

/// <summary>
/// Outcome of applying + committing a step's <see cref="FileChange"/>
/// batch. <see cref="CommitSha"/> is null for local workspaces; for git
/// workspaces it's the SHA of the produced commit.
/// </summary>
public sealed record ApplyAndCommitResult(
    /// <summary>Number of changes successfully applied.</summary>
    int AppliedCount,
    /// <summary>Commit SHA for git workspaces; null otherwise.</summary>
    string? CommitSha,
    /// <summary>True when there were no effective changes to commit (e.g. modify-replace produced identical content). Git's "nothing to commit" exit is mapped to this.</summary>
    bool NoCommit
);
