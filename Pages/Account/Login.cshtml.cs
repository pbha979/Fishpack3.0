using System.ComponentModel.DataAnnotations;
using FishPack3._0.Data;
using FishPack3._0.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FishPack3._0.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly UserService users;

    public LoginModel(UserService users) => this.users = users;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // A stale cookie would otherwise send the operator straight back into the application.
        if (User.Identity?.IsAuthenticated == true)
        {
            await CookieSignIn.SignOutAsync(HttpContext);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        UserSignInResult result;
        try
        {
            result = await users.ValidateCredentialsAsync(Input.UserName, Input.Password);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
            return Page();
        }

        if (!result.Succeeded || result.User is null)
        {
            ErrorMessage = result.Status switch
            {
                SignInStatus.Disabled =>
                    "Brukerkontoen er deaktivert. Kontakt en administrator.",
                SignInStatus.LockedOut =>
                    "Kontoen er midlertidig låst etter for mange mislykkede forsøk. Prøv igjen om 15 minutter.",
                _ => "Feil brukernavn eller passord."
            };

            return Page();
        }

        await CookieSignIn.SignInAsync(HttpContext, result.User);

        if (result.User.MustChangePassword)
        {
            return RedirectToPage("/Account/ChangePassword");
        }

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : LocalRedirect("~/");
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Brukernavn er påkrevd.")]
        [Display(Name = "Brukernavn")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Passord er påkrevd.")]
        [DataType(DataType.Password)]
        [Display(Name = "Passord")]
        public string Password { get; set; } = string.Empty;
    }
}
