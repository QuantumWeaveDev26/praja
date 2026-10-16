using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Account;
[EnableRateLimiting("password-change")]
public class ChangePasswordModel(WorkspaceDb db):PageModel
{
    [BindProperty]public string? CurrentPassword {get;set;}
    [BindProperty]public string? NewPassword {get;set;}
    [BindProperty]public string? ConfirmPassword {get;set;}
    public async Task<IActionResult> OnGetAsync()
    {
        if(User.Identity?.IsAuthenticated!=true)return Challenge();
        var user=await db.Operators.AsNoTracking().SingleAsync(o=>o.Id==User.OwnerId());
        return user.MustChangePassword?Page():RedirectToPage("/Work/Index");
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if(User.Identity?.IsAuthenticated!=true)return Challenge();
        var user=await db.Operators.SingleAsync(o=>o.Id==User.OwnerId());
        if(!user.MustChangePassword)return RedirectToPage("/Work/Index");
        string? error=null;
        if(user.IsDisabled||user.LockedUntilUtc>DateTime.UtcNow)error="Unable to change the password. Try signing in again later.";
        else if(string.IsNullOrEmpty(CurrentPassword)||CurrentPassword.Length>128||WorkspaceIdentity.Hasher.VerifyHashedPassword(user,user.PasswordHash,CurrentPassword)==PasswordVerificationResult.Failed)
        {
            user.FailedLogins++;if(user.FailedLogins>=5){user.LockedUntilUtc=DateTime.UtcNow.AddMinutes(15);user.FailedLogins=0;}
            error="The current password is incorrect.";
        }
        else if(NewPassword is null||NewPassword.Length is <14 or >128||NewPassword.Any(char.IsControl)||string.IsNullOrWhiteSpace(NewPassword))error="Choose a password of 14–128 characters without control characters.";
        else if(NewPassword!=ConfirmPassword)error="The new passwords do not match.";
        else if(WorkspaceIdentity.Hasher.VerifyHashedPassword(user,user.PasswordHash,NewPassword)!=PasswordVerificationResult.Failed)error="Choose a different password from your temporary password.";
        if(error is not null)
        {
            try{await db.SaveChangesAsync();}catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();}
            ModelState.Clear();CurrentPassword=NewPassword=ConfirmPassword=null;ModelState.AddModelError("",error);return Page();
        }
        user.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(user,NewPassword!);user.MustChangePassword=false;user.AccountVersion++;user.FailedLogins=0;user.LockedUntilUtc=null;
        db.AccountAudits.Add(new(){ActorId=user.Id,TargetId=user.Id,Action="Officer completed mandatory password change."});
        try{await db.SaveChangesAsync();}
        catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();ModelState.Clear();CurrentPassword=NewPassword=ConfirmPassword=null;ModelState.AddModelError("","Account changed in another session. Sign in again before retrying.");return Page();}
        await WorkspaceIdentity.SignInAsync(HttpContext,user);
        return RedirectToPage("/Work/Index");
    }
}
