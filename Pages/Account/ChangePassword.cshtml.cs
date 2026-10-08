using System.ComponentModel.DataAnnotations;
using FishPack3._0.Data;
using FishPack3._0.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FishPack3._0.Pages.Account;

public class ChangePasswordModel : PageModel
{
    private readonly UserService users;

    public ChangePasswordModel(UserService users) => this.users = users;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public bool IsRequired { get; private set; }

    public IActionResult OnGet()
    {
        IsRequired = User.MustChangePassword();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        IsRequired = User.MustChangePassword();

        var userName = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userName))
        {
            return RedirectToPage("/Account/Login");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var user = await users.ChangePasswordAsync(userName, Input.CurrentPassword, Input.NewPassword);

            // Refresh the cookie so the must-change claim no longer blocks the application.
            await CookieSignIn.SignInAsync(HttpContext, user);
            return LocalRedirect("~/");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            ErrorMessage = exception.Message;
            return Page();
        }
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Nåværende passord er påkrevd.")]
        [DataType(DataType.Password)]
        [Display(Name = "Nåværende passord")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Nytt passord er påkrevd.")]
        [DataType(DataType.Password)]
        [Display(Name = "Nytt passord")]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Bekreft det nye passordet.")]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "Passordene er ikke like.")]
        [Display(Name = "Bekreft nytt passord")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
