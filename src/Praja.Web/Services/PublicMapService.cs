using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;

namespace Praja.Web.Services;

public record PublicMapItem(string Reference, string Department, string DepartmentLabel,
    string Stage, string StageLabel, double ApproximateLatitude, double ApproximateLongitude,
    string CreatedDate, string UpdatedDate, string? DueDate);
public record PublicMapSnapshot(IReadOnlyList<PublicMapItem> Items, int TotalMatching, bool Truncated);
public record PublicProgressEvent(string StageLabel, DateTime AtUtc);
public record PublicHistory(string Reference, string DepartmentLabel, string Stage, string StageLabel,
    DateTime CreatedUtc, DateTime? DueUtc, IReadOnlyList<PublicProgressEvent> Events, bool Truncated);

public class PublicMapService(WorkspaceDb db)
{
    public const int MaximumItems = 500;
    private IQueryable<Grievance> Eligible() => db.Grievances.AsNoTracking().Where(g => g.PublicMapConsent && !g.IsSample
        && (g.Status == "Registered" || g.Status == "InProgress" || g.Status == "ClosureReview" || g.Status == "AwaitingCitizen" || g.Status == "Closed")
        && g.IncidentLatitude.HasValue && g.IncidentLongitude.HasValue
        && g.IncidentLatitude >= -90 && g.IncidentLatitude <= 90 && g.IncidentLongitude >= -180 && g.IncidentLongitude <= 180);

    public async Task<PublicHistory?> HistoryAsync(string? reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 30) return null;
        var item = await Eligible().Where(g => g.Reference == reference)
            .Select(g => new { g.Id, g.Reference, g.Department, g.Status, g.CreatedUtc }).SingleOrDefaultAsync(cancellationToken);
        if (item is null) return null;
        var work = await db.CaseWorkflows.AsNoTracking().Where(w => w.Id == item.Id)
            .Select(w => new { w.Stage, w.DueUtc }).SingleOrDefaultAsync(cancellationToken);
        // Project only controlled stages and timestamps. Never expose actors, free text or evidence.
        var rows = await db.WorkflowUpdates.AsNoTracking().Where(u => u.CaseId == item.Id
            && (u.Stage == "Assigned" || u.Stage == "InProgress" || u.Stage == "OfficerReview"
                || u.Stage == "Verification" || u.Stage == "CitizenConfirmation" || u.Stage == "Closed"))
            .OrderByDescending(u => u.AtUtc).ThenByDescending(u => u.Id)
            .Select(u => new { u.Stage, u.AtUtc }).Take(201).ToListAsync(cancellationToken);
        var events = new List<PublicProgressEvent>();
        string? previous = null;
        foreach (var row in rows.Take(200).Reverse())
        {
            if (row.Stage != previous) events.Add(new(CaseRules.WorkflowLabel(row.Stage), row.AtUtc));
            previous = row.Stage;
        }
        var department = CaseRules.Departments.GetValueOrDefault(item.Department, CaseRules.Departments["review"]);
        var stage = NormalizeStage(work?.Stage, item.Status);
        return new(item.Reference, department, stage, CaseRules.WorkflowLabel(stage), item.CreatedUtc, work?.DueUtc, events, rows.Count > 200);
    }

    public async Task<PublicMapSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        // Consent is separate from complaint processing permission. Only coarse coordinates
        // and controlled labels cross this public boundary; private case text never does.
        var eligible = Eligible();
        var total = await eligible.CountAsync(cancellationToken);
        var rows = await (from g in eligible
            join workflow in db.CaseWorkflows.AsNoTracking() on g.Id equals workflow.Id into workflows
            from w in workflows.DefaultIfEmpty()
            orderby g.CreatedUtc descending, g.Reference
            select new { g.Reference, g.Department, g.Status, g.IncidentLatitude,
                g.IncidentLongitude, g.CreatedUtc, g.UpdatedUtc,
                WorkflowStage = w == null ? null : w.Stage,
                DueUtc = w == null ? (DateTime?)null : w.DueUtc })
            .Take(MaximumItems).ToListAsync(cancellationToken);
        var items = rows.Select(row =>
        {
            var department = CaseRules.Departments.ContainsKey(row.Department) ? row.Department : "review";
            var stage = NormalizeStage(row.WorkflowStage, row.Status);
            return new PublicMapItem(row.Reference, department, CaseRules.Departments[department],
                stage, CaseRules.WorkflowLabel(stage),
                Math.Round(row.IncidentLatitude!.Value, 2, MidpointRounding.AwayFromZero),
                Math.Round(row.IncidentLongitude!.Value, 2, MidpointRounding.AwayFromZero),
                Date(row.CreatedUtc), Date(row.UpdatedUtc), row.DueUtc.HasValue ? Date(row.DueUtc.Value) : null);
        }).ToList();
        return new(items, total, total > MaximumItems);
    }

    private static string NormalizeStage(string? stage, string status) => stage switch
    {
        "Assigned" or "InProgress" or "OfficerReview" or "Verification" or "CitizenConfirmation" or "Closed" => stage,
        _ => status switch
        {
            "InProgress" => "InProgress",
            "ClosureReview" => "Verification",
            "AwaitingCitizen" => "CitizenConfirmation",
            "Closed" => "Closed",
            _ => "Registered"
        }
    };
    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
