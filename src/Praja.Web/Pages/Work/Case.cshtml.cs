using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Work;
[EnableRateLimiting("evidence")]
[RequestSizeLimit(9*1024*1024)]
public class CaseModel(WorkspaceDb db,WorkflowService workflow,EvidenceService evidence,CommunityFeedbackService community):PageModel
{
    public Grievance Case {get;private set;}=null!;
    public IReadOnlyList<CommunityFeedbackReport> CommunityReports {get;private set;}=[];
    public CaseWorkflow? Work {get;private set;}
    public OfficerRole Role {get;private set;}=null!;
    public List<TransferOfficer> TransferOfficers {get;private set;}=[];
    public record TransferOfficer(Guid Id,string Name,string Department);
    public List<Operator> Contractors {get;private set;}=[];
    public List<Operator> Officers {get;private set;}=[];
    public List<WorkflowUpdate> Updates {get;private set;}=[];
    public IReadOnlyList<EvidenceItem> Files {get;private set;}=[];
    public string? AccessCode {get;private set;}
    [BindProperty]public int Version {get;set;}
    [BindProperty]public Guid Officer {get;set;}
    [BindProperty]public DateTime Due {get;set;}=DateTime.UtcNow.AddDays(7).Date;
    [BindProperty]public string? Department {get;set;}
    [BindProperty]public string? Action {get;set;}
    [BindProperty]public string? Note {get;set;}
    [BindProperty]public Guid? Photo {get;set;}
    [BindProperty]public double? Latitude {get;set;}
    [BindProperty]public double? Longitude {get;set;}
    [BindProperty]public DateTime? Captured {get;set;}
    [BindProperty]public bool Checked {get;set;}
    [BindProperty]public IFormFile? Upload {get;set;}
    [BindProperty]public Guid ReportId {get;set;}
    [BindProperty]public int ReportVersion {get;set;}
    [BindProperty]public string? ReviewAction {get;set;}
    [BindProperty]public string? ReviewNote {get;set;}
    [BindProperty]public DateTime? ReviewDue {get;set;}
    public async Task<IActionResult> OnGetAsync(Guid id){if(!await Load(id))return NotFound();return Page();}
    public async Task<IActionResult> OnGetDownloadAsync(Guid id,Guid file)
    {
        if(!await Load(id))return NotFound();var result=await evidence.DownloadAsync(id,file,Case.OwnerId);return result is null?NotFound():File(result.Bytes,"application/octet-stream",result.FileName);
    }
    public async Task<IActionResult> OnPostAsync(Guid id,string handler)
    {
        if(!await Load(id))return NotFound();
        try{
            if(!ModelState.IsValid)throw new InvalidOperationException("Check the entered numbers, dates and required fields.");
            if(handler=="Route")await workflow.RouteAsync(User.OwnerId(),id,Version,Department??"");
            else if(handler=="Assign")await workflow.AssignAsync(User.OwnerId(),id,Officer,Due,Version);
            else if(handler=="Transfer")await workflow.TransferAsync(User.OwnerId(),id,Officer,Version,Note??"");
            else if(handler=="Reassign")await workflow.ReassignAsync(User.OwnerId(),id,Officer,Version,Note??"");
            else if(handler=="AssignContractor")await workflow.AssignContractorAsync(User.OwnerId(),id,Officer,Version,Note??"");
            else if(handler=="CommunityReview")await community.ReviewAsync(User.OwnerId(),id,ReportId,ReportVersion,Version,ReviewAction??"",ReviewNote??"",ReviewDue);
            else if(handler=="Act")await workflow.ActAsync(User.OwnerId(),id,Version,Action??"",Note??"",Photo,Latitude,Longitude,Captured,Checked);
            else if(handler=="Share"){AccessCode=await workflow.IssueAsync(User.OwnerId(),id);await Load(id);return Page();}
            else if(handler=="Upload")
            {
                if(Work?.Stage!="InProgress"||!((Role.Role=="Department"&&Work.AssignedOfficerId==User.OwnerId())||(Role.Role=="Contractor"&&Work.ContractorId==User.OwnerId()&&Work.ContractorAccepted)))return Forbid();
                if(Upload is null)throw new InvalidOperationException("Choose a photo or supporting document.");
                await evidence.UploadAsync(id,Case.OwnerId,User.Identity!.Name!,Version,Upload,Note,Checked,HttpContext.RequestAborted);
            }
            else return BadRequest();
            return RedirectToPage(new{id});
        }catch(UnauthorizedAccessException){return Forbid();}
        catch(InvalidOperationException ex){db.ChangeTracker.Clear();ModelState.Clear();await Load(id);ModelState.AddModelError("",ex.Message);return Page();}
    }
    private async Task<bool> Load(Guid id)
    {
        Case=(await db.Grievances.AsNoTracking().SingleOrDefaultAsync(g=>g.Id==id))!;
        if(Case is null||!await workflow.CanReadAsync(User.OwnerId(),Case))return false;
        Role=await workflow.RoleAsync(User.OwnerId());Work=await db.CaseWorkflows.AsNoTracking().SingleOrDefaultAsync(w=>w.Id==id);
        Officers=await db.Operators.AsNoTracking().Where(o=>!o.IsDisabled&&db.OfficerRoles.Any(r=>r.Id==o.Id&&r.Role=="Department"&&r.Department==Case.Department)).ToListAsync();
        if(Role.Role is "Admin" or "Intake")TransferOfficers=await (from o in db.Operators.AsNoTracking() join r in db.OfficerRoles.AsNoTracking() on o.Id equals r.Id where !o.IsDisabled&&r.Role=="Department"&&r.Department!=Case.Department&&r.Department!="review" orderby r.Department,o.Name select new TransferOfficer(o.Id,o.Name,r.Department)).ToListAsync();
        Contractors=await db.Operators.AsNoTracking().Where(o=>!o.IsDisabled&&db.OfficerRoles.Any(r=>r.Id==o.Id&&r.Role=="Contractor"&&r.Department==Case.Department)).OrderBy(o=>o.Name).ToListAsync();
        Updates=await db.WorkflowUpdates.AsNoTracking().Where(u=>u.CaseId==id).OrderBy(u=>u.AtUtc).ToListAsync();
        if(Role.Role != "Contractor") CommunityReports=await community.ReportsAsync(id);
        Files=await evidence.ListAsync(id,Case.OwnerId);return true;
    }
}
