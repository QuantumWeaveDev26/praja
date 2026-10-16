using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Data;
using Praja.Web.Domain;
using Praja.Web.Services;

namespace Praja.Web.Pages.Grievances;
public class DetailsModel(CaseService cases, CitizenAccessService citizenAccess, EvidenceService evidence) : PageModel
{
    public Grievance Case { get; private set; } = null!;
    public InboxMessage? InboxSource {get;private set;}
    public ClosureAssessment Assessment => CaseRules.AssessClosure(Case.OriginalText, Case.ResolutionReport, Case.EvidenceReference, Case.CitizenFeedback);
    public CitizenAccessCode? IssuedAccess { get; private set; }
    public IReadOnlyList<EvidenceItem> Evidence {get;private set;}=[];
    [BindProperty] public int Version { get; set; }
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? DraftText { get; set; }
    [BindProperty] public string? Department { get; set; }
    [BindProperty] public string? CategoryCode { get; set; }
    [BindProperty] public string? SubcategoryCode { get; set; }
    [BindProperty] public bool Reviewed { get; set; }
    [BindProperty] public string? ResolutionReport { get; set; }
    [BindProperty] public string? EvidenceReference { get; set; }
    [BindProperty] public string? CitizenFeedback { get; set; }
    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await Load(id)) return NotFound();
        Populate(); return Page();
    }
    public Task<IActionResult> OnPostRegisterAsync(Guid id) => Mutate(id, () => cases.RegisterAsync(Case, Version, Title ?? "", DraftText ?? "", Department ?? "", Reviewed, User.Identity!.Name!,
        Request.Form.ContainsKey(nameof(CategoryCode)) ? CategoryCode ?? "" : null,
        Request.Form.ContainsKey(nameof(SubcategoryCode)) ? SubcategoryCode ?? "" : null), preserveRegistration: true);
    public Task<IActionResult> OnPostStartAsync(Guid id) => Mutate(id, () => cases.StartAsync(Case, Version, User.Identity!.Name!));
    public Task<IActionResult> OnPostReviewAsync(Guid id) => Mutate(id, () => cases.ReviewAsync(Case, Version, ResolutionReport ?? "", EvidenceReference ?? "", CitizenFeedback ?? "Pending", User.Identity!.Name!));
    public Task<IActionResult> OnPostCloseAsync(Guid id) => Mutate(id, () => cases.CloseAsync(Case, Version, Reviewed, User.Identity!.Name!));
    public async Task<IActionResult> OnPostShareAsync(Guid id)
    {
        if (!await Load(id)) return NotFound();
        if (Case.Status == "Draft") return BadRequest("Register the complaint before issuing citizen access.");
        var code = await citizenAccess.IssueAsync(id, User.OwnerId(), cancellation: HttpContext.RequestAborted);
        if (code is null) return NotFound();
        try
        {
            await cases.RecordCitizenAccessAsync(Case, Version, User.Identity!.Name!);
            IssuedAccess = code;
            ModelState.Clear(); Populate(); return Page();
        }
        catch (InvalidOperationException ex)
        {
            if (!await Load(id)) return NotFound();
            ModelState.Clear(); Populate(); ModelState.AddModelError("", ex.Message); return Page();
        }
    }
    private async Task<IActionResult> Mutate(Guid id, Func<Task> action, bool preserveRegistration = false)
    {
        if (!await Load(id)) return NotFound();
        var submittedVersion = Version;
        try { await action(); return RedirectToPage(new { id }); }
        catch (InvalidOperationException ex)
        {
            ModelState.Clear();
            if (!await Load(id)) return NotFound();
            if (preserveRegistration && Case.Status == "Draft" && Case.Version == submittedVersion)
                Reviewed = false;
            else Populate();
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
    }
    private async Task<bool> Load(Guid id)
    {
        Case=(await cases.FindAsync(id,User.OwnerId()))!;
        if(Case is null)return false;
        InboxSource=await cases.FindInboxSourceAsync(id,User.OwnerId());
        Evidence=await evidence.ListAsync(id,User.OwnerId(),HttpContext.RequestAborted); return true;
    }
    private void Populate()
    {
        Version = Case.Version; Title = Case.Title; DraftText = Case.DraftText; Department = Case.Department;
        CategoryCode = Case.CategoryCode; SubcategoryCode = Case.SubcategoryCode;
        ResolutionReport = Case.ResolutionReport; EvidenceReference = Case.EvidenceReference; CitizenFeedback = Case.CitizenFeedback;
    }
}
