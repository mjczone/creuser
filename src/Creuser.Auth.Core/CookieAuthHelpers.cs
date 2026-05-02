using System.Security.Claims;
using Creuser.Auth.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Creuser.Auth.Core;

public static class CookieAuthHelpers
{
    public const string SchemeName = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string MustChangePasswordClaim = "must_change_password";

    public static Task SignInAsync(HttpContext http, User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(MustChangePasswordClaim, user.MustChangePassword ? "1" : "0"),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return http.SignInAsync(SchemeName, new ClaimsPrincipal(identity));
    }

    public static Guid? GetUserId(HttpContext http)
    {
        var raw = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
