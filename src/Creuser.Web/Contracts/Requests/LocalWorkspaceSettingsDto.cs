namespace Creuser.Web.Contracts.Requests;

/// <summary>
/// Wire shape for local-filesystem workspace settings. Mirrors
/// <see cref="Creuser.Core.Repositories.LocalWorkspaceSettings"/>.
/// </summary>
public sealed record LocalWorkspaceSettingsDto(string Path, bool Writable = true);
