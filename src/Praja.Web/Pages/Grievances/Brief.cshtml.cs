using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Data;
using Praja.Web.Services;

namespace Praja.Web.Pages.Grievances;

[RequestSizeLimit(64 * 1024)]
[EnableRateLimiting("intake")]
public class BriefModel(CaseService cases, CaseBriefService briefs, EvidenceService evidence, DraftingService drafting) : PageModel
{
    public Grievance Case { get; private set; } = null!;
    public IReadOnlyList<EvidenceItem> Files { get; private set; } = [];
    public CaseBriefSnapshot? Snapshot { get; private set; }
    public bool AiConnected => drafting.IsConnected;
    [BindProperty] public int Version { get; set; }
    [BindProperty] public bool UseAi { get; set; }
    [BindProperty] public bool ProcessingConsent { get; set; }
    [BindProperty] public List<Guid> SelectedEvidence { get; set; } = [];
    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await Load(id)) return NotFound(); Version = Case.Version; return Page();
    }
    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!await Load(id)) return NotFound();
        if (!ModelState.IsValid) return Page();
        try { Snapshot = await briefs.BuildAsync(id, User.OwnerId(), Version, SelectedEvidence, UseAi, ProcessingConsent, HttpContext.RequestAborted); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); }
        return Page();
    }
    private async Task<bool> Load(Guid id)
    {
        Case = (await cases.FindAsync(id, User.OwnerId()))!;
        if (Case is null) return false;
        Files = await evidence.ListAsync(id, User.OwnerId(), HttpContext.RequestAborted); return true;
    }
}
