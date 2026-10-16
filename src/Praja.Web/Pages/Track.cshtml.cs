using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages;
[EnableRateLimiting("tracking")]
[RequestSizeLimit(16384)]
public class TrackModel(WorkspaceDb db,WorkflowService workflow,EvidenceService evidence):PageModel
{
    private const string Cookie="Praja.CitizenWorkflow";
    public Grievance? Case {get;private set;}
    public CaseWorkflow? Work {get;private set;}
    public List<CaseNotification> Notifications {get;private set;}=[];
    public List<WorkflowUpdate> Updates {get;private set;}=[];
    public async Task OnGetAsync()=>await Load();
    public async Task<IActionResult> OnPostOpenAsync()
    {
        var code=Request.Form["Code"].Count==1?Request.Form["Code"].ToString():null;
        if(Request.Query.ContainsKey("Code")||await workflow.CitizenCaseAsync(code) is null){ModelState.AddModelError("","Code unavailable or expired. Ask the intake officer for a new code.");return Page();}
        Response.Cookies.Append(Cookie,code!,new(){HttpOnly=true,Secure=Request.IsHttps,SameSite=SameSiteMode.Strict,Path="/Track",MaxAge=TimeSpan.FromDays(7)});
        return RedirectToPage();
    }
    public IActionResult OnPostExit(){Response.Cookies.Delete(Cookie,new(){Path="/Track"});return RedirectToPage();}
    public async Task<IActionResult> OnPostHideMapAsync()
    {
        var permitted=await workflow.CitizenCaseAsync(Request.Cookies[Cookie]);
        if(permitted is null)return NotFound();
        var item=await db.Grievances.SingleAsync(g=>g.Id==permitted.Id);
        if(item.PublicMapConsent)
        {
            item.PublicMapConsent=false;item.Version++;
            db.AuditEntries.Add(new(){GrievanceId=item.Id,Actor="Citizen portal",Action="Citizen removed this complaint from the public map."});
            try{await db.SaveChangesAsync();}
            catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();await Load();ModelState.AddModelError("","The case changed. Please retry removing it from the public map.");return Page();}
        }
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostRespondAsync(int version,string? decision,string? note)
    {
        try{
            if(!ModelState.IsValid||decision is not ("Confirm" or "Dispute"))throw new InvalidOperationException("Choose confirm or dispute.");
            await workflow.RespondAsync(Request.Cookies[Cookie],version,decision=="Confirm",note??"");return RedirectToPage();
        }catch(UnauthorizedAccessException){return NotFound();}
        catch(InvalidOperationException ex){db.ChangeTracker.Clear();await Load();ModelState.Clear();ModelState.AddModelError("",ex.Message);return Page();}
    }
    public async Task<IActionResult> OnPostPhotoAsync()
    {
        await Load();if(Work?.CompletionPhotoId is null||Case is null||Work.Stage is not ("CitizenConfirmation" or "Closed"))return NotFound();
        var file=await evidence.DownloadAsync(Case.Id,Work.CompletionPhotoId.Value,Case.OwnerId);return file is null?NotFound():File(file.Bytes,"application/octet-stream",file.FileName);
    }
    public async Task<IActionResult> OnPostIncidentPhotoAsync()
    {
        await Load();
        if(Case?.IncidentPhotoId is not {} photo)return NotFound();
        var file=await evidence.DownloadAsync(Case.Id,photo,Case.OwnerId);
        return file is null?NotFound():File(file.Bytes,"application/octet-stream",file.FileName);
    }
    private async Task Load()
    {
        Case=await workflow.CitizenCaseAsync(Request.Cookies[Cookie]);if(Case is null)return;
        Notifications=await db.CaseNotifications.AsNoTracking().Where(n=>n.CaseId==Case.Id&&n.Recipient=="citizen").OrderByDescending(n=>n.CreatedUtc).ThenByDescending(n=>n.Id).Take(50).ToListAsync();
        Work=await workflow.CitizenAsync(Request.Cookies[Cookie]);if(Work is null)return;
        Updates=await db.WorkflowUpdates.AsNoTracking().Where(u=>u.CaseId==Work.Id).OrderBy(u=>u.AtUtc).ToListAsync();
    }
}
