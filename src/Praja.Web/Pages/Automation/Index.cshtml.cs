using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Automation;
[EnableRateLimiting("inbox")]
public class IndexModel(AutomationStore store,AutomationService automation,GmailConfigurationStore gmail,WorkspaceDb db,IWebHostEnvironment environment):PageModel
{
    public AutomationState State{get;private set;}=new();public bool Connected{get;private set;}public string Mailbox{get;private set;}="";
    public List<(PreparedComplaint Proposal,InboxMessage Message)> Queue{get;private set;}=[];
    [BindProperty]public bool Permission{get;set;}
    [TempData]public string? Notice{get;set;}
    private bool Allowed()=>(environment.IsDevelopment()||environment.IsEnvironment("Testing"))&&(HttpContext.Connection.RemoteIpAddress is{}address?IPAddress.IsLoopback(address):environment.IsEnvironment("Testing"));
    private async Task Load()
    {
        var owner=User.OwnerId();State=store.Read(owner);var account=gmail.Read(owner);Connected=account.Connected;Mailbox=account.Mailbox;
        var ids=State.Prepared.Select(p=>p.InboxId).ToArray();var messages=await db.InboxMessages.AsNoTracking().Where(m=>m.OwnerId==owner&&m.State=="Pending"&&ids.Contains(m.Id)).ToListAsync();
        Queue=State.Prepared.OrderByDescending(p=>p.PreparedUtc).Where(p=>messages.Any(m=>m.Id==p.InboxId)).Select(p=>(p,messages.Single(m=>m.Id==p.InboxId))).ToList();
    }
    public async Task<IActionResult> OnGetAsync(){if(!Allowed())return NotFound();await Load();return Page();}
    public IActionResult OnPostEnable(){if(!Allowed())return NotFound();if(!Permission){Notice="Confirm the one-time processing permission before enabling automation.";return RedirectToPage();}store.Update(User.OwnerId(),s=>s with{Enabled=true,Mailbox=gmail.Read(User.OwnerId()).Mailbox,Policy=Guid.NewGuid(),Status="Enabled. The worker checks every 60 seconds while Praja is running. Use Run now for an immediate check."});return RedirectToPage();}
    public async Task<IActionResult> OnPostRetrySkippedAsync()
    {
        if(!Allowed())return NotFound();
        var owner=User.OwnerId();var gate=store.RunLock(owner);
        if(!await gate.WaitAsync(0)){Notice="A collection run is active. Retry after it finishes.";return RedirectToPage();}
        try
        {
            var mailbox=gmail.Read(owner).Mailbox;
            var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mailbox)))[..24];
            store.Update(owner,s=>s with{
                Seen=s.Seen.Except(s.ManualReview.Where(r=>r.Mailbox==mailbox).Select(r=>"gmail:"+key+":"+r.MessageId)).ToArray(),
                ManualReview=s.ManualReview.Where(r=>r.Mailbox!=mailbox).ToArray()});
            Notice="Skipped emails queued for another check. Enable automation and use Run now.";
        }
        finally{gate.Release();}
        return RedirectToPage();
    }
    public Task<IActionResult> OnPostRecheckComplaintAsync(string messageId)=>ReviewNonComplaint(messageId,true);
    public Task<IActionResult> OnPostDismissNonComplaintAsync(string messageId)=>ReviewNonComplaint(messageId,false);
    private async Task<IActionResult> ReviewNonComplaint(string messageId,bool recheck)
    {
        if(!Allowed())return NotFound();
        var owner=User.OwnerId();var gate=store.RunLock(owner);
        if(!await gate.WaitAsync(0)){Notice="A collection run is active. Try again after it finishes.";return RedirectToPage();}
        try
        {
            var account=gmail.Read(owner);var state=store.Read(owner);
            if(!account.Connected||!state.NonComplaintReview.Any(r=>r.Mailbox==account.Mailbox&&r.MessageId==messageId))return NotFound();
            if(recheck&&!Permission){Notice="Confirm that this email is a complaint and may be retained and processed before rechecking.";return RedirectToPage();}
            var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.Mailbox)))[..24];
            var external="gmail:"+key+":"+messageId;
            store.Update(owner,s=>recheck?s with{ForcedReview=s.ForcedReview.Append(external).Distinct().TakeLast(50).ToArray()}:
                s with{NonComplaintReview=s.NonComplaintReview.Where(r=>r.Mailbox!=account.Mailbox||r.MessageId!=messageId).ToArray(),ForcedReview=s.ForcedReview.Where(id=>id!=external).ToArray()});
            Notice=recheck?"Queued for complaint preparation. Enable automation and choose Run now. Final registration still needs review.":"Review reference dismissed. The email remains in Gmail and will not be imported again automatically.";
        }
        finally{gate.Release();}
        return RedirectToPage();
    }
    public IActionResult OnPostPause(){if(!Allowed())return NotFound();store.Update(User.OwnerId(),s=>s with{Enabled=false,Policy=Guid.NewGuid(),Status="Paused. Prepared drafts and source messages remain available."});return RedirectToPage();}
    public async Task<IActionResult> OnPostRunAsync(){if(!Allowed())return NotFound();try{await automation.RunAsync(User.OwnerId(),HttpContext.RequestAborted);Notice="Collection finished. Review the prepared queue below.";}catch(InvalidOperationException ex){Notice=ex.Message;}return RedirectToPage();}
    public async Task<IActionResult> OnPostDemoAsync(){if(!Allowed())return NotFound();if(!Permission){Notice="Confirm permission to process the three fictional demo posts.";return RedirectToPage();}try{await automation.DemoAsync(User.OwnerId(),HttpContext.RequestAborted);Notice="Social demo finished. These are simulated posts, not live platform results.";}catch(Exception ex)when(ex is InvalidOperationException or IOException){Notice="Demo preparation could not finish. Retained messages remain in the intake inbox for retry.";}return RedirectToPage();}
}
