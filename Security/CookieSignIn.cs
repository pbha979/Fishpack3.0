using System.Globalization;
using System.Security.Claims;
using FishPack3._0.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace FishPack3._0.Security;

/// <summary>
/// Issues the authentication cookie. Kept in one place so the claim set stays
/// identical for sign-in and for the refresh that follows a password change.
/// </summary>
public static class CookieSignIn
{
    public static Task SignInAsync(HttpContext context, ApplicationUser user, bool isPersistent = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role),
            new(FishPackClaimTypes.DisplayName, user.DisplayName),
            new(FishPackClaimTypes.MustChangePassword, user.MustChangePassword ? "true" : "false")
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = isPersistent });
    }

    public static Task SignOutAsync(HttpContext context) =>
        context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}
