namespace Creuser.Core.Repositories;

/// <summary>
/// A configured connection to a content source — a git repository, S3
/// bucket, or local directory the platform reads from and (where the
/// implementation supports it) writes back to. Identified by a stable
/// URL-safe slug used in <c>/w/:slug/...</c> routes.
///
/// The <see cref="Settings"/> string is the type-specific JSON config —
/// for <see cref="WorkspaceType.Git"/> it deserializes to
/// <see cref="GitWorkspaceSettings"/>; future types add their own records.
/// </summary>
public sealed record Workspace(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    string Type,
    string Settings,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy,
    /// <summary>UTC time of the most recent sync attempt — null until the first sync.</summary>
    DateTime? LastSyncAt = null,
    /// <summary>Resolved commit SHA after the last successful sync. Null on failure or for non-git types.</summary>
    string? LastSyncSha = null,
    /// <summary>One of <c>ok</c>, <c>failed</c>, or null (never synced).</summary>
    string? LastSyncStatus = null,
    /// <summary>Free-text message from the last sync — git stderr on failure, success summary on success.</summary>
    string? LastSyncMessage = null
);

public static class WorkspaceType
{
    public const string Git = "git";
    public const string S3 = "s3";
    public const string Local = "local";

    public static bool IsValid(string type) => type is Git or S3 or Local;
}

/// <summary>
/// Mode toggle for git workspaces — whether the platform pushes its
/// commits directly to the working branch or opens a pull request against
/// it. Direct push is the default; PR mode adds a CI tax that operators
/// only want when they have an external review process.
/// </summary>
public static class GitWorkspaceMode
{
    public const string DirectPush = "direct-push";
    public const string PullRequest = "pull-request";
}

public static class GitWorkspacePushFrequency
{
    public const string EveryCommit = "every-commit";
    public const string Batched = "batched";
}

/// <summary>
/// Type-specific configuration for a <see cref="WorkspaceType.Git"/>
/// workspace. Serialized to/from <see cref="Workspace.Settings"/> as JSON.
///
/// <see cref="AuthSecret"/> is the filename of an SSH key or PAT under
/// <c>/data/secrets/</c> — never the credential value itself. The
/// <see cref="AuthMode"/> tells the runtime how to interpret that file:
/// as an HTTPS Personal Access Token, an OpenSSH-format private key, or
/// nothing (public repo).
/// </summary>
public sealed record GitWorkspaceSettings(
    string RepositoryUrl,
    /// <summary>One of <see cref="GitAuthMode.None"/>, <see cref="GitAuthMode.HttpsPat"/>, <see cref="GitAuthMode.SshKey"/>.</summary>
    string AuthMode = GitAuthMode.None,
    /// <summary>Filename under <c>/data/secrets/</c> holding the PAT (https-pat) or OpenSSH-format private key (ssh-key). Ignored when <see cref="AuthMode"/> is <see cref="GitAuthMode.None"/>.</summary>
    string? AuthSecret = null,
    /// <summary>Branch the platform commits to. Architecture default is <c>creuser/main</c>; consumer apps can override (COMPAS uses <c>compas/development</c>).</summary>
    string WorkingBranch = "creuser/main",
    /// <summary>Branch the working branch is rebased / pulled from when admins want fresh source content.</summary>
    string SourceBranch = "main",
    /// <summary>One of <see cref="GitWorkspaceMode.DirectPush"/> or <see cref="GitWorkspaceMode.PullRequest"/>.</summary>
    string Mode = GitWorkspaceMode.DirectPush,
    /// <summary>One of <see cref="GitWorkspacePushFrequency.EveryCommit"/> or <see cref="GitWorkspacePushFrequency.Batched"/>.</summary>
    string PushFrequency = GitWorkspacePushFrequency.EveryCommit
);

public static class GitAuthMode
{
    /// <summary>Public repo, no credentials needed.</summary>
    public const string None = "none";

    /// <summary>HTTPS URL + Personal Access Token (or username:password). Server uses HTTP Basic auth.</summary>
    public const string HttpsPat = "https-pat";

    /// <summary>SSH URL + OpenSSH-format private key. Future support for server-generated keypairs lives under the same mode.</summary>
    public const string SshKey = "ssh-key";

    public static bool IsValid(string mode) => mode is None or HttpsPat or SshKey;
}

/// <summary>
/// Type-specific configuration for a <see cref="WorkspaceType.Local"/>
/// workspace. The simplest implementation: just a server-side filesystem
/// path the platform reads from and (when <see cref="Writable"/> is true)
/// writes back to directly. No staging, no commits, no branches.
///
/// <para>
/// In Docker deployments this points at a mounted volume
/// (e.g. <c>/workspaces/myrepo</c>). In dev / on-host runs it can be any
/// directory the Creuser process has access to. There's no path-allowlist
/// in v1 — single-tenant on-premise + admin-only management means the
/// trust boundary is the admin's own discretion. Multi-tenant deployments
/// (post-v1) would need a path-prefix constraint here.
/// </para>
/// </summary>
public sealed record LocalWorkspaceSettings(
    /// <summary>Absolute filesystem path on the server. Must exist when the workspace is created or updated.</summary>
    string Path,
    /// <summary>When true, the platform may write into the directory. When false, the workspace is read-only — useful for shared documentation trees the platform should index but not mutate.</summary>
    bool Writable = true
);

public interface IWorkspaceStore
{
    Task<Workspace?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<Workspace?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListAsync(int skip, int take, CancellationToken ct = default);
    Task SaveAsync(Workspace workspace, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Update only the sync-state columns for a workspace. Avoids
    /// round-tripping the full record when sync runs.
    /// </summary>
    Task UpdateSyncStatusAsync(
        Guid id,
        DateTime syncedAt,
        string status,
        string? sha,
        string? message,
        CancellationToken ct = default
    );
}
