using FishPack3._0.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FishPack3._0.Pages.Account;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await CookieSignIn.SignOutAsync(HttpContext);
        return RedirectToPage("/Account/Login");
    }
}
