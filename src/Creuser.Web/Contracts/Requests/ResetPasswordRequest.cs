namespace Creuser.Web.Contracts.Requests;

/// <summary>
/// Optional admin-supplied temporary password. When null/empty the server
/// generates a random 12-char password (no visually ambiguous characters).
/// </summary>
public sealed record ResetPasswordRequest(string? TemporaryPassword = null);
