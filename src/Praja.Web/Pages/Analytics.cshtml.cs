using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[Authorize]
public class AnalyticsModel(AnalyticsService analytics) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Period { get; set; } = "30";
    [BindProperty(SupportsGet = true)] public string? District { get; set; }
    [BindProperty(SupportsGet = true)] public string? Department { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeSamples { get; set; }
    public AnalyticsSnapshot? Report { get; private set; }
    public string[] DistrictOptions { get; private set; } = [];
    public DateTime AsOfUtc { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellation)
    {
        AsOfUtc = DateTime.UtcNow;
        if (Period is null) { Period = "30"; ModelState.Remove(nameof(Period)); }
        if (!ModelState.IsValid) return Page();
        try
        {
            var owner = User.OwnerId();
            Report = await analytics.LoadAsync(owner, Filter(), AsOfUtc, cancellation);
            DistrictOptions = await analytics.DistrictOptionsAsync(owner, IncludeSamples, cancellation);
        }
        catch (ArgumentException exception) { ModelState.AddModelError("", exception.Message); }
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken cancellation)
    {
        if (!ModelState.IsValid) return BadRequest("Choose valid analytics filters before exporting.");
        try
        {
            var report = await analytics.LoadAsync(User.OwnerId(), Filter(), DateTime.UtcNow, cancellation);
            return File(Encoding.UTF8.GetBytes(AnalyticsService.ExportCsv(report)), "text/csv; charset=utf-8", "praja-local-analytics.csv");
        }
        catch (ArgumentException) { return BadRequest("Choose valid analytics filters before exporting."); }
    }

    private AnalyticsFilter Filter() => new(Period ?? "30", District, Department, IncludeSamples);
}
