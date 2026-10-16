using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Services;

namespace Praja.Web.Pages.Intake;

// URL-encoded Kannada expands to nine transport bytes per BMP character.
// Keep the logical 32,768-character bound while allowing the form overhead.
[RequestSizeLimit(384 * 1024)]
[EnableRateLimiting("inbox")]
public class ImportModel(InboxImportService imports) : PageModel
{
    [BindProperty] public ImportInput Input { get; set; } = new();
    [BindProperty, StringLength(200_000)] public string PreviewToken { get; set; } = "";
    [BindProperty] public bool Permission { get; set; }
    public IReadOnlyList<ImportPreviewRow> Rows { get; private set; } = [];
    public IReadOnlyList<ImportedMessage>? Results { get; private set; }
    public class ImportInput
    {
        [StringLength(InboxImportService.MaximumCharacters)] public string Csv { get; set; } = "";
    }
    public void OnGet(string? sample) { if (sample == "demo") Input.Csv = InboxImportService.SampleCsv; }
    public IActionResult OnGetSample() => File(Encoding.UTF8.GetBytes(InboxImportService.SampleCsv), "text/csv; charset=utf-8", "praja-fictional-messages.csv");
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        ModelState.Remove(nameof(PreviewToken)); PreviewToken = "";
        if (!ModelState.IsValid) return Page();
        try
        {
            var preview = await imports.PreviewAsync(User.OwnerId(), User.Identity!.Name!, Input.Csv, HttpContext.RequestAborted);
            Rows = preview.Rows; PreviewToken = preview.Token;
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); }
        return Page();
    }
    public async Task<IActionResult> OnPostImportAsync()
    {
        // The confirmed payload comes only from the protected preview, never posted CSV.
        ModelState.Remove("Input.Csv");
        if (!ModelState.IsValid) return Page();
        try
        {
            Input.Csv = imports.ReadPreviewCsv(PreviewToken, User.OwnerId());
            Results = await imports.ImportAsync(User.OwnerId(), User.Identity!.Name!, PreviewToken, Permission, HttpContext.RequestAborted);
            ModelState.Clear(); PreviewToken = ""; Input.Csv = "";
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); }
        catch (DbUpdateException) { ModelState.AddModelError("", "The import could not be saved. No partial batch was retained. Preview and retry these messages."); }
        return Page();
    }
    public IActionResult OnPostEdit()
    {
        if (!ModelState.IsValid) return Page();
        try { Input.Csv = imports.ReadPreviewCsv(PreviewToken, User.OwnerId()); ModelState.Clear(); PreviewToken = ""; }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); }
        return Page();
    }
}
