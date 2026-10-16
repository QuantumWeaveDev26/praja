using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
namespace Praja.Web.Pages;
public class TransparencyModel(WorkspaceDb db):PageModel
{
    public record ServiceCount(string Department,int Received,int Open,int Closed,int AwaitingCitizen,int Overdue);
    public List<ServiceCount> Rows {get;private set;}=[];
    public async Task OnGetAsync()
    {
        var cases=await db.Grievances.AsNoTracking().Where(g=>!g.IsSample&&g.Status!="Draft").Select(g=>new{g.Id,g.Department,g.Status}).ToListAsync();
        var overdue=await db.CaseWorkflows.AsNoTracking().Where(w=>w.Stage!="Closed"&&w.DueUtc<DateTime.UtcNow.Date).Select(w=>w.Id).ToListAsync();
        Rows=cases.GroupBy(g=>g.Department).Select(group=>new ServiceCount(group.Key,group.Count(),group.Count(g=>g.Status!="Closed"),group.Count(g=>g.Status=="Closed"),group.Count(g=>g.Status=="AwaitingCitizen"),group.Count(g=>overdue.Contains(g.Id)))).OrderBy(r=>r.Department).ToList();
    }
}
