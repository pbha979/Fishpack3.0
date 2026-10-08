using FishPack3._0.Data;

namespace FishPack3._0.Security;

/// <summary>
/// Creates the account table and the bootstrap administrator during startup.
/// Failures are logged rather than fatal so the application can still start when
/// the operational database is temporarily unreachable.
/// </summary>
public static class AccountStoreInitializer
{
    public static async Task InitializeAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FishPack.AccountStore");

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        try
        {
            await users.EnsureSchemaAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "The user account table could not be prepared. Sign-in is unavailable until the database is reachable.");
            return;
        }

        var section = app.Configuration.GetSection("InitialAdministrator");
        var userName = section["UserName"];
        var displayName = section["DisplayName"];
        var password = section["Password"];

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No initial administrator is configured. Set InitialAdministrator:UserName and "
                + "InitialAdministrator:Password (for example with dotnet user-secrets) to create the first account.");
            return;
        }

        if (UserService.ValidatePassword(password) is { } passwordError)
        {
            logger.LogError("The configured initial administrator password was rejected: {Reason}", passwordError);
            return;
        }

        try
        {
            var created = await users.EnsureInitialAdministratorAsync(
                userName,
                string.IsNullOrWhiteSpace(displayName) ? userName : displayName,
                password);

            if (created)
            {
                logger.LogWarning(
                    "Created the initial administrator '{UserName}'. The password must be changed at the first sign-in.",
                    userName);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The initial administrator could not be created.");
        }
    }
}
