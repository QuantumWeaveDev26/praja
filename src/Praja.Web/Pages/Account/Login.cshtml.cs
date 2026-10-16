using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;

namespace Praja.Web.Pages.Account;

[EnableRateLimiting("login")]
public class LoginModel(WorkspaceDb db, IWebHostEnvironment environment) : PageModel
{
    [BindProperty, Required, EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [BindProperty, Required, StringLength(128)] public string Password { get; set; } = "";
    private static readonly Operator DummyUser = new();
    private static readonly string DummyHash = WorkspaceIdentity.Hasher.HashPassword(DummyUser, Guid.NewGuid().ToString("N"));
    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Work/Index");
        if (environment.IsDevelopment() && !await db.Operators.AnyAsync()) return RedirectToPage("/Setup");
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!ModelState.IsValid) return Page();
        var user = await db.Operators.SingleOrDefaultAsync(u => u.Email == Email.Trim().ToLowerInvariant());
        var verification = WorkspaceIdentity.Hasher.VerifyHashedPassword(user ?? DummyUser, user?.PasswordHash ?? DummyHash, Password);
        if (user is null || user.IsDisabled || user.LockedUntilUtc > DateTime.UtcNow || verification == PasswordVerificationResult.Failed)
        {
            if (user is not null && !(user.LockedUntilUtc > DateTime.UtcNow))
            {
                user.FailedLogins++;
                if (user.FailedLogins >= 5) { user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(15); user.FailedLogins = 0; }
                await db.SaveChangesAsync();
            }
            ModelState.AddModelError("", "Unable to sign in. Check your credentials or try again later.");
            return Page();
        }
        user.FailedLogins = 0; user.LockedUntilUtc = null;
        if (verification == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = WorkspaceIdentity.Hasher.HashPassword(user, Password);
        await db.SaveChangesAsync();
        await WorkspaceIdentity.SignInAsync(HttpContext, user);
        if(user.MustChangePassword)return RedirectToPage("/Account/ChangePassword");
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Work");
    }
}
