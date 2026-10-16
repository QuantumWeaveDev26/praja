using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Intake;
public class IndexModel(WorkspaceDb db) : PageModel
{
    [BindProperty(SupportsGet=true)] public string State {get;set;}="Pending";
    public List<InboxMessage> Items {get;private set;}=[];
    public async Task OnGetAsync(){
        if(State is not ("Pending" or "Converted" or "Dismissed" or "Duplicate"))State="Pending";
        var owner=User.OwnerId();
        Items=await db.InboxMessages.AsNoTracking().Where(m=>m.OwnerId==owner&&m.State==State).OrderByDescending(m=>m.ReceivedUtc).Take(200).ToListAsync();
    }
}
