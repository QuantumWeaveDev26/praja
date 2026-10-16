using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class PublicMapModel(PublicMapService map) : PageModel
{
    public PublicMapSnapshot Snapshot { get; private set; } = new([], 0, false);

    public async Task OnGetAsync() => Snapshot = await map.GetAsync(HttpContext.RequestAborted);

    public async Task<IActionResult> OnGetDataAsync() => new JsonResult(await map.GetAsync(HttpContext.RequestAborted));
}
