using FishPack3._0.Data;

namespace FishPack3._0.Security;

/// <summary>
/// Confines a signed-in user to the account pages until the required password
/// change has been completed. Blocking the Blazor endpoint prevents a circuit
/// from being established, so no application page can be reached client-side.
/// </summary>
public static class MustChangePasswordMiddleware
{
    private const string ChangePasswordPath = "/Account/ChangePassword";

    private static readonly string[] AllowedPrefixes =
    {
        "/Account",
        "/css",
        "/js",
        "/lib",
        "/_framework",
        "/_content"
    };

    public static IApplicationBuilder UseMustChangePassword(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true
                && context.User.MustChangePassword()
                && !IsAlwaysAllowed(context.Request.Path))
            {
                context.Response.Redirect(ChangePasswordPath);
                return;
            }

            await next();
        });

    private static bool IsAlwaysAllowed(PathString path)
    {
        foreach (var prefix in AllowedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var value = path.Value;
        return value is not null
            && (value.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
    }
}
