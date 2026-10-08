using System.Security.Claims;
using FishPack3._0.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FishPack3._0.Pages.Account;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public string DisplayName { get; private set; } = string.Empty;

    public string RoleDisplayName { get; private set; } = string.Empty;

    public void OnGet()
    {
        DisplayName = User.GetDisplayName();
        RoleDisplayName = UserRoles.GetDisplayName(User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty);
    }
}
