using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Automation;
[EnableRateLimiting("inbox")]
[RequestSizeLimit(128*1024)]
public class ReviewModel(WorkspaceDb db,AutomationStore store,CaseService cases,IWebHostEnvironment environment):PageModel
{
    public PreparedComplaint Proposal{get;private set;}=null!;public InboxMessage Item{get;private set;}=null!;
    [BindProperty]public int Version{get;set;}
    [BindProperty]public string? Title{get;set;}
    [BindProperty]public string? DraftText{get;set;}
    [BindProperty]public string? District{get;set;}
    [BindProperty]public string? Location{get;set;}
    [BindProperty]public string? Department{get;set;}
    [BindProperty]public string? CategoryCode{get;set;}
    [BindProperty]public string? SubcategoryCode{get;set;}
    [BindProperty]public bool Reviewed{get;set;}
    private bool Allowed()=>(environment.IsDevelopment()||environment.IsEnvironment("Testing"))&&(HttpContext.Connection.RemoteIpAddress is{}address?IPAddress.IsLoopback(address):environment.IsEnvironment("Testing"));
    private async Task<bool> Load(Guid id)
    {
        Item=(await db.InboxMessages.SingleOrDefaultAsync(m=>m.Id==id&&m.OwnerId==User.OwnerId()))!;
        Proposal=store.Read(User.OwnerId()).Prepared.SingleOrDefault(p=>p.InboxId==id)!;
        return Item is not null&&(Proposal is not null||Item.GrievanceId.HasValue);
    }
    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if(!Allowed()||!await Load(id))return NotFound();if(Item.GrievanceId is{}caseId)return RedirectToPage("/Grievances/Details",new{id=caseId});
        Version=Item.Version;Title=Proposal.Draft.Title;DraftText=Proposal.Draft.Text;District=Proposal.Analysis.District;Location=Proposal.Analysis.Location;
        Department=Proposal.Analysis.Route.Department;CategoryCode=Proposal.Analysis.Route.Category;SubcategoryCode=Proposal.Analysis.Route.Subcategory;return Page();
    }
    public async Task<IActionResult> OnPostApproveAsync(Guid id)
    {
        if(!Allowed()||!await Load(id))return NotFound();if(Item.GrievanceId is{}caseId)return RedirectToPage("/Grievances/Details",new{id=caseId});
        try
        {
            if(!Reviewed)throw new InvalidOperationException("Review the prepared details and confirm registration.");
            if(Item.State!="Pending"||Item.Version!=Version||Proposal.InboxVersion!=Version||Item.Fingerprint!=Proposal.Fingerprint)throw new InvalidOperationException("This source changed. Refresh the queue before approving.");
            await using var transaction=await db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
            try
            {
                var prepared=Proposal.Draft with{Title=Title??"",Text=DraftText??""};
                var item=await cases.CreateFromInboxAsync(Item.Id,Version,User.OwnerId(),User.Identity!.Name!,Item.OriginalText,Item.Language,District??"",Location??"",true,HttpContext.RequestAborted,Proposal.Analysis.Route,preparedDraft:prepared);
                item.IsSample=Proposal.IsDemo;
                item.History.Add(new(){Actor=User.Identity!.Name!,Action="Automatic extraction and draft preparation reviewed once by officer; no automatic registration or closure was performed."});
                await cases.RegisterAsync(item,item.Version,Title??"",DraftText??"",Department??"review",true,User.Identity!.Name!,CategoryCode??"",SubcategoryCode??"");
                await transaction.CommitAsync(HttpContext.RequestAborted);
                // The case/inbox transaction is authoritative if the optional queue cleanup fails.
                try{store.Update(User.OwnerId(),s=>s with{Prepared=s.Prepared.Where(p=>p.InboxId!=id).ToArray()});}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
                return RedirectToPage("/Grievances/Details",new{id=item.Id});
            }
            catch{await transaction.RollbackAsync(CancellationToken.None);db.ChangeTracker.Clear();throw;}
        }
        catch(Exception ex)when(ex is InvalidOperationException or DbUpdateException)
        {ModelState.Clear();Reviewed=false;if(!await Load(id))return NotFound();ModelState.AddModelError("",ex is InvalidOperationException?ex.Message:"The source changed or could not be saved. No partial registration was retained. Refresh and retry.");return Page();}
    }
}
