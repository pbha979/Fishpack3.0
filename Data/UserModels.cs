using System.Security.Claims;

namespace FishPack3._0.Data;

/// <summary>
/// Role identifiers are stored in the database in an invariant form so that display
/// language can change without migrating data.
/// </summary>
public static class UserRoles
{
    public const string Administrator = "Administrator";
    public const string Supervisor = "Supervisor";
    public const string Operator = "Operator";

    public static readonly IReadOnlyList<string> All = new[] { Administrator, Supervisor, Operator };

    public static string GetDisplayName(string role) => role switch
    {
        Administrator => "Administrator",
        Supervisor => "Arbeidsleder",
        Operator => "Operatør",
        _ => role
    };

    public static bool IsKnown(string? role) =>
        role is not null && All.Contains(role, StringComparer.Ordinal);
}

public static class AuthorizationPolicies
{
    /// <summary>Create and manage user accounts. Administrators only.</summary>
    public const string UserAdministration = "UserAdministration";

    /// <summary>OTM import and reporting. Administrators and supervisors.</summary>
    public const string Supervision = "Supervision";

    /// <summary>The ULD workflow. Available to every active role.</summary>
    public const string Operations = "Operations";
}

public static class FishPackClaimTypes
{
    public const string DisplayName = "fishpack:display-name";
    public const string MustChangePassword = "fishpack:must-change-password";
}

public static class ClaimsPrincipalExtensions
{
    public static string GetDisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirst(FishPackClaimTypes.DisplayName)?.Value
            ?? principal.Identity?.Name
            ?? string.Empty;

    public static bool MustChangePassword(this ClaimsPrincipal principal) =>
        string.Equals(
            principal.FindFirst(FishPackClaimTypes.MustChangePassword)?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);
}

public sealed record ApplicationUser(
    int UserId,
    string UserName,
    string DisplayName,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    int FailedLoginCount,
    DateTime? LockedOutUntil,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? ModifiedAt,
    string? ModifiedBy)
{
    public string RoleDisplayName => UserRoles.GetDisplayName(Role);

    public bool IsLockedOut(DateTime utcNow) => LockedOutUntil is { } until && until > utcNow;
}

public enum SignInStatus
{
    Success,
    InvalidCredentials,
    Disabled,
    LockedOut
}

public sealed record UserSignInResult(SignInStatus Status, ApplicationUser? User)
{
    public bool Succeeded => Status == SignInStatus.Success && User is not null;

    public static UserSignInResult Failed(SignInStatus status) => new(status, null);
}
