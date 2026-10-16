using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;

namespace Praja.Web.Pages.Connections;
[EnableRateLimiting("ai-settings")]
public class GmailCallbackModel(GmailService gmail):PageModel
{
    public async Task<IActionResult> OnGetAsync(string? state,string? code,string? error)
    {
        try{await gmail.CompleteAsync(User.OwnerId(),state,code,error,HttpContext.RequestAborted);TempData["GmailNotice"]="Connected to the configured mailbox. Create its complaint label in Gmail and preview messages here.";}
        catch(Exception ex)when(ex is InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or ArgumentException or IOException or System.Security.Cryptography.CryptographicException)
        {TempData["GmailNotice"]=ex is InvalidOperationException?ex.Message:"Gmail connection could not be completed. Check configuration and connect again.";}
        // Immediately leave the code-bearing URL; never render or persist the code.
        return RedirectToPage("/GmailSettings");
    }
}
