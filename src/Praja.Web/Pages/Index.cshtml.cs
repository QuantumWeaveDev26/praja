using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;

namespace Praja.Web.Pages;
public class IndexModel(WorkspaceDb db, DraftingService drafting) : PageModel
{
    public List<Grievance> Cases { get; private set; } = [];
    public int Active => Cases.Count(c => c.Status != "Closed");
    public int Drafts => Cases.Count(c => c.Status == "Draft");
    public int Reviews => Cases.Count(c => c.Status == "ClosureReview");
    public int Closed => Cases.Count(c => c.Status == "Closed");
    public bool AiConnected => drafting.IsConnected;
    public async Task OnGetAsync() { var owner = User.OwnerId(); Cases = await db.Grievances.AsNoTracking().Where(g => g.OwnerId == owner).OrderByDescending(g => g.UpdatedUtc).ToListAsync(); }
}
