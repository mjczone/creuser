using Creuser.Core.Execution;
using Creuser.Core.Repositories;

namespace Creuser.Cli;

/// <summary>
/// Resolves the workspace root from a <c>--workspace</c> flag or the cwd
/// (walking up until a <c>.creuser/</c> directory is found). The CLI runs
/// against an in-memory <see cref="Workspace"/> stub since it doesn't
/// authenticate against the API.
/// </summary>
public static class WorkspaceResolver
{
    public static (Workspace Workspace, string Path) ResolveOrThrow(string? explicitPath)
    {
        var path = ResolvePath(explicitPath);
        if (path is null)
        {
            throw new CliUserError(
                "Could not locate a workspace root. Run from inside a workspace directory or pass --workspace <path>."
            );
        }
        var workspace = StubWorkspace(path);
        return (workspace, path);
    }

    public static IWorkspaceWorkingTree TreeFor(string path) => new LocalTree(path);

    private static string? ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            var rooted = Path.GetFullPath(explicitPath);
            return Directory.Exists(rooted) ? rooted : null;
        }
        var cur = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(cur))
        {
            if (Directory.Exists(Path.Combine(cur, ".creuser")))
                return cur;
            var parent = Directory.GetParent(cur);
            if (parent is null)
                return null;
            cur = parent.FullName;
        }
        return null;
    }

    private static Workspace StubWorkspace(string path)
    {
        // The editor + scanner only touch fields the working-tree resolver returns.
        // A stub workspace with the resolved path embedded in slug/name is enough.
        return new Workspace(
            Id: Guid.NewGuid(),
            Slug: "cli",
            Name: "CLI workspace",
            Description: null,
            Type: "local",
            Settings: "{}",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            CreatedBy: null
        );
    }

    private sealed class LocalTree : IWorkspaceWorkingTree
    {
        private readonly string _path;

        public LocalTree(string path)
        {
            _path = path;
        }

        public Task<string?> ResolvePathAsync(
            Workspace workspace,
            CancellationToken ct = default
        ) => Task.FromResult<string?>(_path);

        public Task<ApplyAndCommitResult> ApplyAndCommitAsync(
            Workspace workspace,
            string workingTreePath,
            IReadOnlyList<FileChange> changes,
            string commitMessage,
            CancellationToken ct = default
        ) => throw new NotSupportedException("CLI does not commit through ApplyAndCommit.");

        public Task<string?> ResolveHeadShaAsync(
            Workspace workspace,
            string workingTreePath,
            CancellationToken ct = default
        ) => Task.FromResult<string?>(null);
    }
}
