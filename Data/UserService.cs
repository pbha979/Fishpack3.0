using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace FishPack3._0.Data;

public sealed class UserService
{
    public const int MinimumPasswordLength = 10;

    private const int MaximumFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private const string UserColumns = """
        USER_ID, USERNAME, DISPLAY_NAME, ROLE, IS_ACTIVE, MUST_CHANGE_PASSWORD,
        FAILED_LOGIN_COUNT, LOCKED_OUT_UNTIL, LAST_LOGIN_AT, CREATED_AT, CREATED_BY,
        MODIFIED_AT, MODIFIED_BY
        """;

    private readonly string connectionString;
    private readonly IPasswordHasher<ApplicationUser> passwordHasher;
    private readonly ILogger<UserService> logger;

    public UserService(
        IConfiguration configuration,
        IPasswordHasher<ApplicationUser> passwordHasher,
        ILogger<UserService> logger)
    {
        connectionString = configuration.GetConnectionString("FishPack")
            ?? throw new InvalidOperationException("The FishPack SQL connection string is not configured.");
        this.passwordHasher = passwordHasher;
        this.logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.APP_USER', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.APP_USER
                (
                    USER_ID              int IDENTITY(1, 1) NOT NULL,
                    USERNAME             nvarchar(100) NOT NULL,
                    DISPLAY_NAME         nvarchar(200) NOT NULL,
                    PASSWORD_HASH        nvarchar(400) NOT NULL,
                    ROLE                 nvarchar(50) NOT NULL,
                    IS_ACTIVE            bit NOT NULL CONSTRAINT DF_APP_USER_IS_ACTIVE DEFAULT (1),
                    MUST_CHANGE_PASSWORD bit NOT NULL CONSTRAINT DF_APP_USER_MUST_CHANGE DEFAULT (0),
                    FAILED_LOGIN_COUNT   int NOT NULL CONSTRAINT DF_APP_USER_FAILED DEFAULT (0),
                    LOCKED_OUT_UNTIL     datetime2(0) NULL,
                    LAST_LOGIN_AT        datetime2(0) NULL,
                    CREATED_AT           datetime2(0) NOT NULL,
                    CREATED_BY           nvarchar(200) NOT NULL,
                    MODIFIED_AT          datetime2(0) NULL,
                    MODIFIED_BY          nvarchar(200) NULL,
                    CONSTRAINT PK_APP_USER PRIMARY KEY (USER_ID),
                    CONSTRAINT UQ_APP_USER_USERNAME UNIQUE (USERNAME)
                );
            END
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the bootstrap administrator when the account store is still empty.
    /// Returns <c>true</c> when an account was created.
    /// </summary>
    public async Task<bool> EnsureInitialAdministratorAsync(
        string userName,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var countCommand = new SqlCommand("SELECT COUNT(*) FROM dbo.APP_USER", connection);
        if (Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        const string sql = """
            INSERT INTO dbo.APP_USER
                (USERNAME, DISPLAY_NAME, PASSWORD_HASH, ROLE, IS_ACTIVE, MUST_CHANGE_PASSWORD,
                 FAILED_LOGIN_COUNT, CREATED_AT, CREATED_BY)
            VALUES (@userName, @displayName, @passwordHash, @role, 1, 1, 0, @createdAt, @createdBy)
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userName", userName.Trim());
        command.Parameters.AddWithValue("@displayName", displayName.Trim());
        command.Parameters.AddWithValue("@passwordHash", HashPassword(userName.Trim(), password));
        command.Parameters.AddWithValue("@role", UserRoles.Administrator);
        command.Parameters.AddWithValue("@createdAt", now);
        command.Parameters.AddWithValue("@createdBy", "System");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<UserSignInResult> ValidateCredentialsAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return UserSignInResult.Failed(SignInStatus.InvalidCredentials);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var stored = await ReadCredentialAsync(connection, userName.Trim(), cancellationToken);
        if (stored is null)
        {
            return UserSignInResult.Failed(SignInStatus.InvalidCredentials);
        }

        var now = DateTime.UtcNow;
        if (!stored.User.IsActive)
        {
            return UserSignInResult.Failed(SignInStatus.Disabled);
        }

        if (stored.User.IsLockedOut(now))
        {
            return UserSignInResult.Failed(SignInStatus.LockedOut);
        }

        var verification = VerifyPassword(stored.User, stored.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            var lockedOut = await RegisterFailedAttemptAsync(connection, stored.User, now, cancellationToken);
            return UserSignInResult.Failed(lockedOut ? SignInStatus.LockedOut : SignInStatus.InvalidCredentials);
        }

        var refreshedHash = verification == PasswordVerificationResult.SuccessRehashNeeded
            ? HashPassword(stored.User.UserName, password)
            : null;

        await RegisterSuccessfulLoginAsync(connection, stored.User.UserId, now, refreshedHash, cancellationToken);

        return new UserSignInResult(
            SignInStatus.Success,
            stored.User with { FailedLoginCount = 0, LockedOutUntil = null, LastLoginAt = now });
    }

    public async Task<IReadOnlyList<ApplicationUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"SELECT {UserColumns} FROM dbo.APP_USER ORDER BY IS_ACTIVE DESC, DISPLAY_NAME";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var users = new List<ApplicationUser>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    public async Task<ApplicationUser?> FindByUserNameAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"SELECT {UserColumns} FROM dbo.APP_USER WHERE USERNAME = @userName";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userName", userName.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<ApplicationUser> CreateUserAsync(
        string userName,
        string displayName,
        string role,
        string password,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        userName = (userName ?? string.Empty).Trim();
        displayName = (displayName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("Brukernavn er påkrevd.", nameof(userName));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Visningsnavn er påkrevd.", nameof(displayName));
        }

        if (!UserRoles.IsKnown(role))
        {
            throw new ArgumentException("Ukjent rolle.", nameof(role));
        }

        if (ValidatePassword(password) is { } passwordError)
        {
            throw new ArgumentException(passwordError, nameof(password));
        }

        var now = DateTime.UtcNow;
        const string sql = """
            INSERT INTO dbo.APP_USER
                (USERNAME, DISPLAY_NAME, PASSWORD_HASH, ROLE, IS_ACTIVE, MUST_CHANGE_PASSWORD,
                 FAILED_LOGIN_COUNT, CREATED_AT, CREATED_BY)
            OUTPUT INSERTED.USER_ID
            VALUES (@userName, @displayName, @passwordHash, @role, 1, 1, 0, @createdAt, @createdBy)
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userName", userName);
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.AddWithValue("@passwordHash", HashPassword(userName, password));
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@createdAt", now);
        command.Parameters.AddWithValue("@createdBy", RequireActor(createdBy));

        int userId;
        try
        {
            userId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new InvalidOperationException($"Brukernavnet '{userName}' er allerede i bruk.");
        }

        return new ApplicationUser(
            userId, userName, displayName, role, true, true, 0, null, null, now, RequireActor(createdBy), null, null);
    }

    public async Task UpdateUserAsync(
        int userId,
        string displayName,
        string role,
        bool isActive,
        string modifiedBy,
        CancellationToken cancellationToken = default)
    {
        displayName = (displayName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Visningsnavn er påkrevd.", nameof(displayName));
        }

        if (!UserRoles.IsKnown(role))
        {
            throw new ArgumentException("Ukjent rolle.", nameof(role));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var losesAdministratorAccess = !isActive || role != UserRoles.Administrator;
            if (losesAdministratorAccess
                && await IsCurrentlyActiveAdministratorAsync(connection, transaction, userId, cancellationToken)
                && await CountOtherActiveAdministratorsAsync(connection, transaction, userId, cancellationToken) == 0)
            {
                throw new InvalidOperationException(
                    "Systemet må ha minst én aktiv administrator. Gi en annen bruker administratorrollen først.");
            }

            const string sql = """
                UPDATE dbo.APP_USER
                SET DISPLAY_NAME = @displayName,
                    ROLE = @role,
                    IS_ACTIVE = @isActive,
                    FAILED_LOGIN_COUNT = CASE WHEN @isActive = 1 THEN FAILED_LOGIN_COUNT ELSE 0 END,
                    LOCKED_OUT_UNTIL = CASE WHEN @isActive = 1 THEN LOCKED_OUT_UNTIL ELSE NULL END,
                    MODIFIED_AT = @modifiedAt,
                    MODIFIED_BY = @modifiedBy
                WHERE USER_ID = @userId
                """;

            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@displayName", displayName);
            command.Parameters.AddWithValue("@role", role);
            command.Parameters.AddWithValue("@isActive", isActive);
            command.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@modifiedBy", RequireActor(modifiedBy));

            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Brukeren ble ikke funnet.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Sets a new password on behalf of an administrator and forces the owner to
    /// choose their own password at the next sign-in.
    /// </summary>
    public async Task ResetPasswordAsync(
        int userId,
        string newPassword,
        string modifiedBy,
        CancellationToken cancellationToken = default)
    {
        if (ValidatePassword(newPassword) is { } passwordError)
        {
            throw new ArgumentException(passwordError, nameof(newPassword));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var lookup = new SqlCommand("SELECT USERNAME FROM dbo.APP_USER WHERE USER_ID = @userId", connection);
        lookup.Parameters.AddWithValue("@userId", userId);
        if (await lookup.ExecuteScalarAsync(cancellationToken) is not string userName)
        {
            throw new InvalidOperationException("Brukeren ble ikke funnet.");
        }

        const string sql = """
            UPDATE dbo.APP_USER
            SET PASSWORD_HASH = @passwordHash,
                MUST_CHANGE_PASSWORD = 1,
                FAILED_LOGIN_COUNT = 0,
                LOCKED_OUT_UNTIL = NULL,
                MODIFIED_AT = @modifiedAt,
                MODIFIED_BY = @modifiedBy
            WHERE USER_ID = @userId
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@passwordHash", HashPassword(userName, newPassword));
        command.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("@modifiedBy", RequireActor(modifiedBy));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Changes the signed-in user's own password after verifying the current one.
    /// </summary>
    public async Task<ApplicationUser> ChangePasswordAsync(
        string userName,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (ValidatePassword(newPassword) is { } passwordError)
        {
            throw new ArgumentException(passwordError, nameof(newPassword));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var stored = await ReadCredentialAsync(connection, userName.Trim(), cancellationToken);
        if (stored is null || !stored.User.IsActive)
        {
            throw new InvalidOperationException("Brukeren ble ikke funnet eller er deaktivert.");
        }

        if (VerifyPassword(stored.User, stored.PasswordHash, currentPassword)
            == PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("Nåværende passord er feil.");
        }

        if (VerifyPassword(stored.User, stored.PasswordHash, newPassword)
            != PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("Det nye passordet må være forskjellig fra det nåværende.");
        }

        const string sql = """
            UPDATE dbo.APP_USER
            SET PASSWORD_HASH = @passwordHash,
                MUST_CHANGE_PASSWORD = 0,
                FAILED_LOGIN_COUNT = 0,
                LOCKED_OUT_UNTIL = NULL,
                MODIFIED_AT = @modifiedAt,
                MODIFIED_BY = @modifiedBy
            WHERE USER_ID = @userId
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", stored.User.UserId);
        command.Parameters.AddWithValue("@passwordHash", HashPassword(stored.User.UserName, newPassword));
        command.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("@modifiedBy", stored.User.UserName);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return stored.User with { MustChangePassword = false };
    }

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
        {
            return $"Passordet må ha minst {MinimumPasswordLength} tegn.";
        }

        if (!password.Any(char.IsUpper))
        {
            return "Passordet må inneholde minst én stor bokstav.";
        }

        if (!password.Any(char.IsLower))
        {
            return "Passordet må inneholde minst én liten bokstav.";
        }

        if (!password.Any(char.IsDigit))
        {
            return "Passordet må inneholde minst ett siffer.";
        }

        return null;
    }

    private string HashPassword(string userName, string password)
    {
        var placeholder = new ApplicationUser(
            0, userName, userName, UserRoles.Operator, true, false, 0, null, null, DateTime.UtcNow, "System", null, null);
        return passwordHasher.HashPassword(placeholder, password);
    }

    private PasswordVerificationResult VerifyPassword(
        ApplicationUser user,
        string passwordHash,
        string password)
    {
        try
        {
            return passwordHasher.VerifyHashedPassword(user, passwordHash, password);
        }
        catch (FormatException exception)
        {
            logger.LogError(exception, "The stored password hash for user '{UserName}' is invalid.", user.UserName);
            throw new InvalidOperationException(
                "Passorddataene for brukeren er ugyldige. Kontakt en administrator.",
                exception);
        }
    }

    private static async Task<bool> IsCurrentlyActiveAdministratorAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM dbo.APP_USER
            WHERE USER_ID = @userId AND ROLE = @role AND IS_ACTIVE = 1
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@role", UserRoles.Administrator);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<int> CountOtherActiveAdministratorsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM dbo.APP_USER WITH (UPDLOCK, HOLDLOCK)
            WHERE ROLE = @role AND IS_ACTIVE = 1 AND USER_ID <> @userId
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@role", UserRoles.Administrator);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> RegisterFailedAttemptAsync(
        SqlConnection connection,
        ApplicationUser user,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var attempts = user.FailedLoginCount + 1;
        var lockedOut = attempts >= MaximumFailedAttempts;

        const string sql = """
            UPDATE dbo.APP_USER
            SET FAILED_LOGIN_COUNT = @attempts,
                LOCKED_OUT_UNTIL = @lockedOutUntil
            WHERE USER_ID = @userId
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", user.UserId);
        command.Parameters.AddWithValue("@attempts", lockedOut ? 0 : attempts);
        command.Parameters.AddWithValue("@lockedOutUntil", lockedOut ? now.Add(LockoutDuration) : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return lockedOut;
    }

    private static async Task RegisterSuccessfulLoginAsync(
        SqlConnection connection,
        int userId,
        DateTime now,
        string? refreshedHash,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE dbo.APP_USER
            SET FAILED_LOGIN_COUNT = 0,
                LOCKED_OUT_UNTIL = NULL,
                LAST_LOGIN_AT = @lastLoginAt
                {(refreshedHash is null ? string.Empty : ", PASSWORD_HASH = @passwordHash")}
            WHERE USER_ID = @userId
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@lastLoginAt", now);
        if (refreshedHash is not null)
        {
            command.Parameters.AddWithValue("@passwordHash", refreshedHash);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<StoredCredential?> ReadCredentialAsync(
        SqlConnection connection,
        string userName,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {UserColumns}, PASSWORD_HASH FROM dbo.APP_USER WHERE USERNAME = @userName";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userName", userName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredCredential(ReadUser(reader), reader.GetString(reader.GetOrdinal("PASSWORD_HASH")));
    }

    private static ApplicationUser ReadUser(SqlDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("USER_ID")),
        reader.GetString(reader.GetOrdinal("USERNAME")),
        reader.GetString(reader.GetOrdinal("DISPLAY_NAME")),
        reader.GetString(reader.GetOrdinal("ROLE")),
        reader.GetBoolean(reader.GetOrdinal("IS_ACTIVE")),
        reader.GetBoolean(reader.GetOrdinal("MUST_CHANGE_PASSWORD")),
        reader.GetInt32(reader.GetOrdinal("FAILED_LOGIN_COUNT")),
        GetNullableDateTime(reader, "LOCKED_OUT_UNTIL"),
        GetNullableDateTime(reader, "LAST_LOGIN_AT"),
        reader.GetDateTime(reader.GetOrdinal("CREATED_AT")),
        reader.GetString(reader.GetOrdinal("CREATED_BY")),
        GetNullableDateTime(reader, "MODIFIED_AT"),
        GetNullableString(reader, "MODIFIED_BY"));

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    private static string? GetNullableString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string RequireActor(string actor) =>
        string.IsNullOrWhiteSpace(actor)
            ? throw new ArgumentException("Brukeren som utfører endringen må være kjent.", nameof(actor))
            : actor.Trim();

    private sealed record StoredCredential(ApplicationUser User, string PasswordHash);
}
