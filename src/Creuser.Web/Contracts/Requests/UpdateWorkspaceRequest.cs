namespace Creuser.Web.Contracts.Requests;

/// <summary>
/// Update an existing workspace's name, description, type, and settings.
/// Slug is intentionally excluded — it's the URL identifier and renaming
/// would break operator bookmarks, in-flight chat links, dashboard
/// references, and saga records pointing at the old slug.
///
/// Type CAN change (e.g. an admin migrates a local workspace to a git
/// workspace), but it requires the corresponding settings field to be
/// populated. Validators enforce that.
/// </summary>
public sealed record UpdateWorkspaceRequest(
    string Name,
    string? Description,
    string Type,
    GitWorkspaceSettingsDto? GitSettings = null,
    LocalWorkspaceSettingsDto? LocalSettings = null
);
