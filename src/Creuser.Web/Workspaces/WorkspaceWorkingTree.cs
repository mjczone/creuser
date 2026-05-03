using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;

namespace Creuser.Web.Workspaces;

/// <summary>
/// Default <see cref="IWorkspaceWorkingTree"/> implementation. Resolves git
/// workspaces to the <see cref="WorkspaceFilesystemService"/>-managed clone
/// under <c>&lt;dataDir&gt;/workspaces/&lt;slug&gt;/</c>, and local
/// workspaces to the operator-configured path on the
/// <see cref="LocalWorkspaceSettings"/> record.
///
/// <para>
/// Also owns the apply-and-commit transaction the executor uses after each
/// step that produced <see cref="FileChange"/> records. For git workspaces
/// the apply path stages + commits via shell-out (consistent with the
/// existing sync code in <c>WorkspacesEndpoints</c>); for local workspaces
/// it just writes the bytes to disk — there's no git history to commit
/// into.
/// </para>
/// </summary>
public sealed class WorkspaceWorkingTree : IWorkspaceWorkingTree
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly WorkspaceFilesystemService _fs;

    public WorkspaceWorkingTree(WorkspaceFilesystemService fs)
    {
        _fs = fs;
    }

    public Task<string?> ResolvePathAsync(Workspace workspace, CancellationToken ct = default)
    {
        if (workspace.Type == WorkspaceType.Git)
        {
            // Note: returning the path doesn't guarantee the clone exists.
            // Step runners that operate on the working tree are responsible
            // for handling "directory not present" — a workspace that's
            // never been synced has no clone yet.
            return Task.FromResult<string?>(_fs.GetWorkingTreePath(workspace.Slug));
        }

        if (workspace.Type == WorkspaceType.Local)
        {
            try
            {
                var settings = JsonSerializer.Deserialize<LocalWorkspaceSettings>(
                    workspace.Settings,
                    JsonOpts
                );
                return Task.FromResult<string?>(settings?.Path);
            }
            catch (JsonException)
            {
                return Task.FromResult<string?>(null);
            }
        }

        // s3 + future types: not yet implemented; runners that ask for the
        // path get null and fail with a clear "no working tree available"
        // message.
        return Task.FromResult<string?>(null);
    }

    public async Task<ApplyAndCommitResult> ApplyAndCommitAsync(
        Workspace workspace,
        string workingTreePath,
        IReadOnlyList<FileChange> changes,
        string commitMessage,
        CancellationToken ct = default
    )
    {
        if (changes.Count == 0)
            return new ApplyAndCommitResult(AppliedCount: 0, CommitSha: null, NoCommit: true);

        if (string.IsNullOrEmpty(workingTreePath) || !Directory.Exists(workingTreePath))
            throw new InvalidOperationException(
                $"Working tree path is not present: '{workingTreePath}'. Sync the workspace before running file-mutating steps."
            );

        // Apply phase — same for git + local. Each op is idempotent enough
        // that a failure halfway through leaves the working tree in a known
        // partial state; we deliberately don't roll back because git's
        // checkpoint will be the prior commit SHA (for git) and the
        // file-mutate runner is expected to either succeed wholly or
        // surface the error so the operator can resolve it.
        var applied = 0;
        foreach (var change in changes)
        {
            await ApplyOneAsync(workingTreePath, change, ct);
            applied++;
        }

        if (workspace.Type != WorkspaceType.Git)
        {
            // Local-type workspaces don't have git history; just return.
            return new ApplyAndCommitResult(
                AppliedCount: applied,
                CommitSha: null,
                NoCommit: false
            );
        }

        // Git workspaces: stage everything + commit. Per-step commit is the
        // unit of audit (see architecture.md "File mutation discipline").
        var add = await RunGitAsync(workingTreePath, ["add", "-A"], ct);
        if (add.ExitCode != 0)
            throw new InvalidOperationException(
                $"`git add -A` failed in working tree {workingTreePath}: {add.Stderr.Trim()}"
            );

        var commit = await RunGitAsync(
            workingTreePath,
            ["commit", "-m", commitMessage, "--allow-empty-message"],
            ct
        );
        if (commit.ExitCode != 0)
        {
            // "nothing to commit, working tree clean" — net-zero changes
            // (modify-replace produced identical content, etc.). Not an
            // error; surface as NoCommit=true so the executor records it
            // distinctly.
            var stderrCombined = (commit.Stdout + "\n" + commit.Stderr).ToLowerInvariant();
            if (stderrCombined.Contains("nothing to commit"))
            {
                return new ApplyAndCommitResult(
                    AppliedCount: applied,
                    CommitSha: null,
                    NoCommit: true
                );
            }
            throw new InvalidOperationException(
                $"`git commit` failed in working tree {workingTreePath}: {commit.Stderr.Trim()}"
            );
        }

        var rev = await RunGitAsync(workingTreePath, ["rev-parse", "HEAD"], ct);
        var sha = rev.ExitCode == 0 ? rev.Stdout.Trim() : null;
        return new ApplyAndCommitResult(AppliedCount: applied, CommitSha: sha, NoCommit: false);
    }

    public async Task<string?> ResolveHeadShaAsync(
        Workspace workspace,
        string workingTreePath,
        CancellationToken ct = default
    )
    {
        if (workspace.Type != WorkspaceType.Git)
            return null;
        if (string.IsNullOrEmpty(workingTreePath) || !Directory.Exists(workingTreePath))
            return null;
        var rev = await RunGitAsync(workingTreePath, ["rev-parse", "HEAD"], ct);
        if (rev.ExitCode != 0)
            return null;
        var sha = rev.Stdout.Trim();
        return string.IsNullOrEmpty(sha) ? null : sha;
    }

    private static async Task ApplyOneAsync(
        string workingTree,
        FileChange change,
        CancellationToken ct
    )
    {
        // All paths are validated to be within the working tree by the
        // runner that produced the FileChange records. Defensive: reject
        // any escape attempt here as a belt-and-suspenders check.
        var fullPath = ResolveSafe(workingTree, change.Path);

        switch (change.Op)
        {
            case FileChangeOp.Create:
            case FileChangeOp.Modify:
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(fullPath, change.Content ?? Array.Empty<byte>(), ct);
                break;
            }
            case FileChangeOp.Delete:
            {
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
                break;
            }
            case FileChangeOp.Rename:
            {
                if (string.IsNullOrEmpty(change.RenameTo))
                    throw new InvalidOperationException(
                        $"Rename change for {change.Path} is missing RenameTo."
                    );
                var destFull = ResolveSafe(workingTree, change.RenameTo);
                var destDir = Path.GetDirectoryName(destFull);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                if (File.Exists(destFull))
                    File.Delete(destFull);
                File.Move(fullPath, destFull);
                if (change.Content is not null)
                    await File.WriteAllBytesAsync(destFull, change.Content, ct);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown FileChangeOp: {change.Op}");
        }
    }

    private static string ResolveSafe(string root, string relative)
    {
        // Trim any leading separator so Path.Combine doesn't anchor to root.
        var trimmed = relative.TrimStart('/', '\\');
        var combined = Path.GetFullPath(Path.Combine(root, trimmed));
        var normalizedRoot = Path.GetFullPath(root);
        if (
            !combined.StartsWith(
                normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? normalizedRoot
                    : normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
            && combined != normalizedRoot
        )
            throw new InvalidOperationException($"Path '{relative}' escapes the workspace root.");
        return combined;
    }

    private sealed record GitOutcome(int ExitCode, string Stdout, string Stderr);

    private static async Task<GitOutcome> RunGitAsync(
        string workingTree,
        IReadOnlyList<string> args,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingTree,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // Configure a deterministic identity for platform-produced commits
        // so they're identifiable and don't depend on the host's git config.
        psi.Environment["GIT_AUTHOR_NAME"] = "Creuser";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "noreply@creuser.local";
        psi.Environment["GIT_COMMITTER_NAME"] = "Creuser";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "noreply@creuser.local";

        using var proc =
            Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadAllAsync(proc.StandardOutput, stdout, ct);
        var stderrTask = ReadAllAsync(proc.StandardError, stderr, ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch { }
            return new GitOutcome(124, stdout.ToString(), "git command timed out after 2 minutes.");
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        return new GitOutcome(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task ReadAllAsync(
        StreamReader reader,
        StringBuilder sink,
        CancellationToken ct
    )
    {
        var buf = new char[4096];
        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (n == 0)
                return;
            sink.Append(buf, 0, n);
        }
    }
}
