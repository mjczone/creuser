namespace Creuser.Web.Contracts.Requests;

/// <summary>
/// Create a new workspace. The polymorphic settings are split into typed
/// fields rather than a discriminated union so the generated TypeScript
/// stays crisp — callers populate the field that matches <see cref="Type"/>
/// and leave the others null.
///
/// <para>
/// Validators enforce that the matching settings field is present for the
/// given type and that unrelated settings fields are null.
/// </para>
/// </summary>
public sealed record CreateWorkspaceRequest(
    string Slug,
    string Name,
    string? Description,
    /// <summary>One of <c>git</c>, <c>local</c>. <c>s3</c> is reserved.</summary>
    string Type,
    GitWorkspaceSettingsDto? GitSettings = null,
    LocalWorkspaceSettingsDto? LocalSettings = null
);
