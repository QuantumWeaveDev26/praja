using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[RequestSizeLimit(16 * 1024)]
[EnableRateLimiting("ai-settings")]
public class PublicSourcesModel(PublicSourceSettings settings, PublicSourceCollection collection, IWebHostEnvironment environment) : PageModel
{
    [BindProperty] public string Query { get; set; } = "Bengaluru water";
    [BindProperty] public string? ApiKey { get; set; }
    [BindProperty] public Guid Version { get; set; }
    [BindProperty] public string Source { get; set; } = "hindu-karnataka";
    [BindProperty] public bool Permission { get; set; }
    [TempData] public string? Notice { get; set; }
    public bool KeyPresent { get; private set; }
    public string Status { get; private set; } = "";
    private bool Allowed() => (environment.IsDevelopment() || environment.IsEnvironment("Testing")) &&
        (HttpContext.Connection.RemoteIpAddress is { } ip ? IPAddress.IsLoopback(ip) : environment.IsEnvironment("Testing"));
    private void Load()
    {
        var state = settings.Read(User.OwnerId());
        ModelState.Clear(); ApiKey = null; Query = state.Query; Version = state.Version;
        KeyPresent = state.YouTubeKey.Length > 0; Status = state.Status;
    }
    public IActionResult OnGet() { if (!Allowed()) return NotFound(); Load(); return Page(); }
    public Task<IActionResult> OnPostSaveAsync() => Save(false);
    public Task<IActionResult> OnPostRemoveKeyAsync() => Save(true);
    private async Task<IActionResult> Save(bool remove)
    {
        if (!Allowed()) return NotFound();
        var gate = settings.RunLock(User.OwnerId());
        if (!await gate.WaitAsync(0)) { Notice = "Collection is active. Try again after it finishes."; return RedirectToPage(); }
        try
        {
            var current = settings.Read(User.OwnerId());
            if (current.Version != Version) throw new InvalidOperationException("Settings changed. Reload before saving.");
            settings.Save(User.OwnerId(), ApiKey, remove ? current.Query : Query ?? "", remove);
            Notice = remove ? "YouTube key removed. Existing inbox entries remain." : "Settings saved. News feeds work without a YouTube key. No request was made.";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException)
        { Load(); ModelState.AddModelError("", ex is InvalidOperationException ? ex.Message : "Could not save local settings."); return Page(); }
        finally { ApiKey = null; ModelState.Remove(nameof(ApiKey)); gate.Release(); }
    }
    public async Task<IActionResult> OnPostCollectAsync()
    {
        if (!Allowed()) return NotFound();
        if (!Permission) { Load(); ModelState.AddModelError("", "Confirm collection into the private review inbox."); return Page(); }
        try
        {
            var result = await collection.RunAsync(User.OwnerId(), Source, Version, HttpContext.RequestAborted);
            Notice = result.Status; return RedirectToPage();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException)
        { Load(); ModelState.AddModelError("", ex is InvalidOperationException ? ex.Message : "Could not complete collection. Retry manually."); return Page(); }
    }
}
