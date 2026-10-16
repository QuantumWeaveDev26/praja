using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[RequestSizeLimit(32 * 1024)]
[EnableRateLimiting("ai-settings")]
public class XSettingsModel(XConfigurationStore settings, XCollectionService collection, IWebHostEnvironment environment) : PageModel
{
    [BindProperty] public SettingsInput Input { get; set; } = new();
    [BindProperty] public bool Permission { get; set; }
    [BindProperty] public Guid Version { get; set; }
    [TempData] public string? Notice { get; set; }
    public bool Configured { get; private set; }
    public bool HasNextPage { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string Status { get; private set; } = "Not configured";
    public class SettingsInput
    {
        public string? BearerToken { get; set; }
        public string Query { get; set; } = XConfigurationStore.DefaultQuery;
    }
    private bool Allowed() => (environment.IsDevelopment() || environment.IsEnvironment("Testing")) &&
        (HttpContext.Connection.RemoteIpAddress is { } ip ? IPAddress.IsLoopback(ip) : environment.IsEnvironment("Testing"));
    private void Load()
    {
        var state = settings.Read(User.OwnerId());
        ModelState.Clear();
        Input = new() { Query = state.Query };
        Version = state.Version; Configured = state.Configured;
        HasNextPage = !string.IsNullOrEmpty(state.NextToken);
        LastSuccessUtc = state.LastSuccessUtc; Status = state.Status;
    }
    public IActionResult OnGet() { if (!Allowed()) return NotFound(); Load(); return Page(); }
    public async Task<IActionResult> OnPostSaveAsync() => await Change(false);
    public async Task<IActionResult> OnPostDisconnectAsync() => await Change(true);
    private async Task<IActionResult> Change(bool disconnect)
    {
        if (!Allowed()) return NotFound();
        var gate = settings.RunLock(User.OwnerId());
        if (!await gate.WaitAsync(0)) { Notice = "A search is active. Try again after it finishes."; return RedirectToPage(); }
        try
        {
            if (Version != settings.Read(User.OwnerId()).Version) throw new InvalidOperationException("Settings changed. Reload this page before saving.");
            if (disconnect) settings.Disconnect(User.OwnerId());
            else settings.Save(User.OwnerId(), Input.BearerToken, Input.Query ?? "");
            Notice = disconnect ? "Saved token removed. Existing intake records remain. Revoke the token in X if needed." : "Settings saved. No X request was made. Run a search to verify access.";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            var message = ex is InvalidOperationException ? ex.Message : "Settings could not be saved. Check local file access.";
            Load(); ModelState.AddModelError("", message); return Page();
        }
        finally { Input.BearerToken = null; ModelState.Remove("Input.BearerToken"); gate.Release(); }
    }
    public Task<IActionResult> OnPostRunAsync() => Run(false);
    public Task<IActionResult> OnPostNextAsync() => Run(true);
    private async Task<IActionResult> Run(bool next)
    {
        if (!Allowed()) return NotFound();
        if (!Permission) { Load(); ModelState.AddModelError("", "Confirm permission and possible X API charges before searching."); return Page(); }
        if (Version != settings.Read(User.OwnerId()).Version) { Load(); ModelState.AddModelError("", "Settings changed. Review the saved query and try again."); return Page(); }
        try
        {
            var result = await collection.RunAsync(User.OwnerId(), next, HttpContext.RequestAborted, expectedVersion: Version);
            Notice = result.Status;
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            var message = ex is InvalidOperationException ? ex.Message : "Search could not finish. Check local file access and retry.";
            Load(); ModelState.AddModelError("", message); return Page();
        }
    }
}
