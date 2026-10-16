using System.Text;
using System.Text.RegularExpressions;
using Praja.Web.Data;
namespace Praja.Web.Services;

public record DepartmentPerformance(string Department,string Label,int Total,int Open,int Closed,int Unassigned,int Overdue,int AwaitingCitizen,int Reopened,int TimedClosures,double? AverageClosureHours,double? OldestOpenDays);
public record RecurringComplaint(string Department,string District,string Location,string Issue,int Count,int Open,int Reopened,int AfterClosure,Guid[] CaseIds);

public partial class AnalyticsService
{
    public static IReadOnlyList<DepartmentPerformance> Performance(AnalyticsSnapshot snapshot,IEnumerable<CaseWorkflow> workflows,DateTime nowUtc)
    {
        nowUtc=Utc(nowUtc);
        var work=workflows.GroupBy(w=>w.Id).ToDictionary(g=>g.Key,g=>g.First());
        return snapshot.Rows.Where(g=>g.Status!="Draft").GroupBy(g=>g.Department).Select(group=>
        {
            var rows=group.ToArray();var open=rows.Where(g=>g.Status!="Closed").ToArray();
            var durations=rows.Where(g=>ValidClosure(g,nowUtc)).Select(g=>(g.ClosedUtc!.Value-g.CreatedUtc).TotalHours).ToArray();
            return new DepartmentPerformance(group.Key,DepartmentLabel(group.Key),rows.Length,open.Length,rows.Length-open.Length,
                open.Count(g=>g.Status=="Registered"&&!work.ContainsKey(g.Id)),
                open.Count(g=>work.TryGetValue(g.Id,out var w)&&w.DueUtc>=g.CreatedUtc&&w.DueUtc<nowUtc),
                open.Count(g=>g.Status=="AwaitingCitizen"),
                rows.Count(g=>work.TryGetValue(g.Id,out var w)&&ValidReopen(g,w,nowUtc)),
                durations.Length,durations.Length==0?null:durations.Average(),
                open.Length==0?null:open.Max(g=>Math.Max(0,(nowUtc-g.CreatedUtc).TotalDays)));
        }).OrderByDescending(g=>g.Open).ThenBy(g=>g.Label,StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<RecurringComplaint> Recurrences(AnalyticsSnapshot snapshot,IEnumerable<CaseWorkflow> workflows,DateTime nowUtc)
    {
        nowUtc=Utc(nowUtc);
        var work=workflows.GroupBy(w=>w.Id).ToDictionary(g=>g.Key,g=>g.First());
        static string Key(string value)=>Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(),@"\s+"," ",RegexOptions.None,TimeSpan.FromMilliseconds(100)).ToUpperInvariant();
        return snapshot.Rows.Where(g=>g.Status!="Draft"&&g.Department!="review"&&!string.IsNullOrWhiteSpace(g.District)&&!string.IsNullOrWhiteSpace(g.Location)&&!string.IsNullOrWhiteSpace(g.SubcategoryCode))
            .GroupBy(g=>new{g.Department,District=Key(g.District),Location=Key(g.Location),Issue=g.SubcategoryCode})
            .Where(g=>g.Count()>=2).Select(group=>
            {
                var rows=group.OrderBy(g=>g.CreatedUtc).ThenBy(g=>g.Id).ToArray();var first=rows[0];
                var earliestClosure=rows.Where(g=>ValidClosure(g,nowUtc)).Select(g=>g.ClosedUtc).Min();
                return new RecurringComplaint(group.Key.Department,first.District.Trim(),first.Location.Trim(),group.Key.Issue,rows.Length,
                    rows.Count(g=>g.Status!="Closed"),rows.Count(g=>work.TryGetValue(g.Id,out var w)&&ValidReopen(g,w,nowUtc)),
                    rows.Count(g=>earliestClosure<g.CreatedUtc),rows.Select(g=>g.Id).ToArray());
            }).OrderByDescending(g=>g.Count).ThenBy(g=>g.Department,StringComparer.Ordinal).ThenBy(g=>g.District,StringComparer.Ordinal).ThenBy(g=>g.Location,StringComparer.Ordinal).ToArray();
    }
    private static bool ValidClosure(Grievance g,DateTime now)=>g.Status=="Closed"&&g.ClosedUtc>=g.CreatedUtc&&g.ClosedUtc<=now;
    private static bool ValidReopen(Grievance g,CaseWorkflow w,DateTime now)=>w.ReopenedUtc>=g.CreatedUtc&&w.ReopenedUtc<=now;
}
