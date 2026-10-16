using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;

namespace Praja.Web.Pages.Grievances;
public class IndexModel(WorkspaceDb db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Department { get; set; }
    public List<Grievance> Cases { get; private set; } = [];
    public async Task OnGetAsync()
    {
        var owner = User.OwnerId();
        var query = db.Grievances.AsNoTracking().Where(g => g.OwnerId == owner);
        if (!string.IsNullOrWhiteSpace(Query)) { var term = Query.Trim()[..Math.Min(Query.Trim().Length, 200)]; query = query.Where(g => g.Title.Contains(term) || g.Reference.Contains(term) || g.OriginalText.Contains(term) || g.District.Contains(term)); }
        if (!string.IsNullOrEmpty(Status)) query = query.Where(g => g.Status == Status);
        if (!string.IsNullOrEmpty(Department)) query = query.Where(g => g.Department == Department);
        Cases = await query.OrderByDescending(g => g.UpdatedUtc).Take(200).ToListAsync();
    }
}
