namespace Creuser.Web.Contracts.Requests;

/// <summary>
/// Wire shape for git workspace settings. Mirrors
/// <see cref="Creuser.Core.Repositories.GitWorkspaceSettings"/> but lives
/// in the Web project so it can carry FluentValidation rules without
/// pulling validation into the domain layer.
///
/// <para>
/// On <c>create</c>/<c>update</c>: <see cref="AuthCredential"/> carries the
/// inline PAT or private-key text. The server moves it to
/// <c>/data/secrets/workspace-&lt;slug&gt;.{pat|key}</c> and stores only the
/// filename in <see cref="AuthSecret"/>. The credential value is never
/// echoed back in any GET response.
/// </para>
/// <para>
/// On <c>get</c>/<c>list</c>: <see cref="AuthCredential"/> is always null,
/// and <see cref="AuthSecret"/> is the persisted filename reference.
/// </para>
/// </summary>
public sealed record GitWorkspaceSettingsDto(
    string RepositoryUrl,
    /// <summary>Auth mode — one of `none`, `https-pat`, `ssh-key`.</summary>
    string AuthMode = "none",
    /// <summary>Filename under /data/secrets/ holding the credential. Set in responses; ignored on writes (the server picks the filename based on the workspace slug).</summary>
    string? AuthSecret = null,
    /// <summary>Inline credential value sent on create/update. Server moves this to disk and clears the response copy. Null on update means "don't change the existing credential".</summary>
    string? AuthCredential = null,
    string WorkingBranch = "creuser/main",
    string SourceBranch = "main",
    string Mode = "direct-push",
    string PushFrequency = "every-commit"
);
