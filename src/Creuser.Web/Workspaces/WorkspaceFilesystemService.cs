namespace Creuser.Web.Workspaces;

/// <summary>
/// Owns the on-disk working tree for git workspaces. Each workspace gets a
/// directory under <c>&lt;dataDir&gt;/workspaces/&lt;slug&gt;/</c>; the sync
/// endpoint clones into it on first sync and fetch-resets it afterwards. The
/// delete endpoint calls <see cref="RemoveWorkingTreeAsync"/> to reclaim the
/// space when the workspace row goes away.
///
/// <para>
/// Local-type workspaces don't use this service — their path is supplied by
/// the admin and Creuser is just a reader / writer over it.
/// </para>
/// </summary>
public sealed class WorkspaceFilesystemService
{
    public string RootPath { get; }

    public WorkspaceFilesystemService(string dataDir)
    {
        RootPath = Path.Combine(dataDir, "workspaces");
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>Absolute path to the working tree for the given slug. Does not check existence.</summary>
    public string GetWorkingTreePath(string slug) => Path.Combine(RootPath, slug);

    /// <summary>True if the slug already has a clone (a <c>.git</c> directory under its working tree).</summary>
    public bool WorkingTreeExists(string slug)
    {
        var dir = GetWorkingTreePath(slug);
        return Directory.Exists(Path.Combine(dir, ".git"));
    }

    /// <summary>
    /// Best-effort recursive delete of a workspace's working tree. Returns
    /// true if the directory was removed (or never existed). Used by the
    /// workspace-delete handler so removing the row also reclaims the disk.
    /// </summary>
    public Task<bool> RemoveWorkingTreeAsync(string slug, CancellationToken ct = default)
    {
        var dir = GetWorkingTreePath(slug);
        if (!Directory.Exists(dir))
            return Task.FromResult(true);
        try
        {
            Directory.Delete(dir, recursive: true);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
