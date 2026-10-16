using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[EnableRateLimiting("community")]
[RequestSizeLimit(8192)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class CommunityModel(WorkspaceDb db, CommunityFeedbackService feedback, IDataProtectionProvider protection) : PageModel
{
    private const string Cookie = "Praja.CommunityBrowser";
    private readonly IDataProtector protector = protection.CreateProtector("Praja.CommunityBrowser.v1");
    public PublicMapItem? Case { get; private set; }
    public CommunityFeedbackSummary Summary { get; private set; } = new(0, 0);
    public CommunityBrowserFeedback BrowserFeedback { get; private set; } = new(1, false, null, null);
    [TempData] public bool Submitted { get; set; }
    [BindProperty] public string Reference { get; set; } = "";
    [BindProperty] public string? Decision { get; set; }
    [BindProperty] public string? Note { get; set; }
    [BindProperty] public bool Consent { get; set; }
    [BindProperty] public double? Latitude { get; set; }
    [BindProperty] public double? Longitude { get; set; }
    [BindProperty] public double? Accuracy { get; set; }
    [BindProperty] public int? FeedbackRound { get; set; }

    public async Task<IActionResult> OnGetAsync(string? reference)
    {
        Reference = reference ?? "";
        if (!await Load()) return NotFound();
        FeedbackRound = BrowserFeedback.CurrentRound;
        if (BrowserId() is null)
            Response.Cookies.Append(Cookie, protector.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))),
                new() { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/Community", MaxAge = TimeSpan.FromDays(365), IsEssential = true });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await Load()) return NotFound();
        var browser = BrowserId();
        if (browser is null) return BadRequest();
        if (!Consent) ModelState.AddModelError("", "Confirm that you inspected the work and agree to send your location for the proximity check.");
        if (FeedbackRound is null) ModelState.AddModelError("", "Refresh this page to report on the current repair round.");
        if (Latitude is null || Longitude is null || Accuracy is null) ModelState.AddModelError("", "Capture your current location before sending feedback.");
        if (ModelState.IsValid)
        {
            try
            {
                await feedback.SubmitAsync(Reference, browser, Decision ?? "", Note, Latitude!.Value, Longitude!.Value, Accuracy!.Value, FeedbackRound);
                Submitted = true;
                return RedirectToPage(new { reference = Reference });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { ModelState.AddModelError("", ex.Message); }
        }
        // Never reflect precise submitted coordinates in the response HTML.
        Latitude = Longitude = Accuracy = null;
        ModelState.Remove(nameof(Latitude)); ModelState.Remove(nameof(Longitude)); ModelState.Remove(nameof(Accuracy));
        FeedbackRound = BrowserFeedback.CurrentRound; ModelState.Remove(nameof(FeedbackRound));
        return Page();
    }

    private string? BrowserId()
    {
        var value = Request.Cookies[Cookie];
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024) return null;
        try { var id = protector.Unprotect(value); return id.Length == 64 && id.All(Uri.IsHexDigit) ? id : null; }
        catch (CryptographicException) { return null; }
    }

    private async Task<bool> Load()
    {
        if (string.IsNullOrWhiteSpace(Reference) || Reference.Length > 30 || await feedback.GetEligibleAsync(Reference) is null) return false;
        var item = await db.Grievances.AsNoTracking().SingleAsync(g => g.Reference == Reference);
        var work = await db.CaseWorkflows.AsNoTracking().SingleAsync(w => w.Id == item.Id);
        var department = CaseRules.Departments.ContainsKey(item.Department) ? item.Department : "review";
        Case = new(item.Reference, department, CaseRules.Departments[department], work.Stage, CaseRules.WorkflowLabel(work.Stage),
            Math.Round(item.IncidentLatitude!.Value, 2, MidpointRounding.AwayFromZero), Math.Round(item.IncidentLongitude!.Value, 2, MidpointRounding.AwayFromZero),
            item.CreatedUtc.ToString("yyyy-MM-dd"), item.UpdatedUtc.ToString("yyyy-MM-dd"), work.DueUtc.ToString("yyyy-MM-dd"));
        Summary = await feedback.SummaryAsync(item.Id);
        var browser = BrowserId();
        BrowserFeedback = browser is null ? new(work.FeedbackRound, false, null, null) : await feedback.GetBrowserFeedbackAsync(item.Id, browser);
        return true;
    }
}
