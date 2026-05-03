namespace Creuser.Web.Contracts.Requests;

public sealed record CreateJobScriptRequest(
    string Slug,
    string Name,
    string? Description,
    string Pattern,
    string Frontmatter,
    string Body,
    string Status
);

public sealed record UpdateJobScriptRequest(
    string Name,
    string? Description,
    string Pattern,
    string Frontmatter,
    string Body,
    string Status
);

public sealed record RunJobScriptRequest(
    /// <summary>Per-run parameters merged with the script's frontmatter <c>inputs:</c> defaults.</summary>
    IDictionary<string, object?>? Parameters
);
