using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class PublicHistoryModel(PublicMapService map) : PageModel
{
    public PublicHistory History { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(string? reference)
    {
        var history = await map.HistoryAsync(reference, HttpContext.RequestAborted);
        if (history is null) return NotFound();
        History = history;
        return Page();
    }
}
