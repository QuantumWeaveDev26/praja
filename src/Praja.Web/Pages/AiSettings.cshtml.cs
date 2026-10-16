using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[RequestSizeLimit(16_000)]
[EnableRateLimiting("ai-settings")]
public class AiSettingsModel(AiConfigurationStore settings, DraftingService drafts, IWebHostEnvironment environment) : PageModel
{
    [BindProperty] public SettingsInput Input { get; set; } = new();
    public bool HasCredential { get; private set; }
    public string? Notice { get; private set; }
    public IActionResult OnGet()
    {
        if (!Allowed()) return NotFound();
        Load(); return Page();
    }
    public IActionResult OnPostSave()
    {
        if (!Allowed()) return NotFound();
        try
        {
            settings.SaveOpenAi(Input.ApiKey, Input.Model ?? "", Input.EnableSpeech, Input.SpeechModel);
            Notice = "Configuration saved. Test drafting to verify this key and model.";
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { ModelState.AddModelError("", "Configuration could not be saved. Check local file access and retry."); }
        // Password tag helpers can otherwise redisplay a posted secret via ModelState.
        Input.ApiKey = null; ModelState.Remove("Input.ApiKey");
        HasCredential = settings.Read().ApiKey.Length > 0;
        return Page();
    }
    public IActionResult OnPostDisable()
    {
        if (!Allowed()) return NotFound();
        ModelState.Clear();
        try { settings.Disable(); Notice = "AI disabled. Drafting now uses local templates."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { ModelState.AddModelError("", "Configuration could not be changed. Retry after checking local file access."); }
        Load(); return Page();
    }
    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!Allowed()) return NotFound();
        ModelState.Clear(); Load();
        if (!drafts.IsConnected) { Notice = "Save the API key and model before testing."; return Page(); }
        var result = await drafts.DraftAsync("Synthetic connection test: No drinking water in Sample Ward for five days. Please investigate and restore supply.", "English", "Bengaluru Urban", "Sample Ward", HttpContext.RequestAborted);
        Notice = result.Method.StartsWith("AI draft", StringComparison.Ordinal) ? "Drafting test passed. A valid AI draft was received for synthetic text; no case was created. OCR and speech still need separate accuracy checks." : "Drafting test failed. Check the key, model access, API billing and network, then retry. The app retained its local template fallback.";
        return Page();
    }
    private bool Allowed() => (environment.IsDevelopment() || environment.IsEnvironment("Testing")) && (HttpContext.Connection.RemoteIpAddress is { } address ? IPAddress.IsLoopback(address) : environment.IsEnvironment("Testing"));
    private void Load()
    {
        var current = settings.Read();
        HasCredential = current.ApiKey.Length > 0;
        Input = new() { Model = string.IsNullOrEmpty(current.Model) ? "gpt-4o-mini" : current.Model, EnableSpeech = current.SpeechConfigured, SpeechModel = string.IsNullOrEmpty(current.TranscriptionModel) ? "gpt-4o-mini-transcribe" : current.TranscriptionModel };
    }
    public class SettingsInput
    {
        public string? ApiKey { get; set; }
        public string? Model { get; set; } = "gpt-4o-mini";
        public bool EnableSpeech { get; set; }
        public string? SpeechModel { get; set; } = "gpt-4o-mini-transcribe";
    }
}
