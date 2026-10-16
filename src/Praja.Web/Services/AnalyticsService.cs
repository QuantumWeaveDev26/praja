using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;

namespace Praja.Web.Services;

public record AnalyticsFilter(string Period = "30", string? District = null, string? Department = null, bool IncludeSamples = false);
public record AnalyticsGroup(string Label, int Volume, int Open, int Closed);
public record AnalyticsDay(DateTime DayUtc, int Volume);
public record AnalyticsFlag(string Label, int Count);
public record AnalyticsSnapshot(IReadOnlyList<Grievance> Rows, int Volume, int Open, int Closed,
    double? AverageResolutionHours, int TimedClosures, int SampleCount,
    IReadOnlyList<AnalyticsGroup> Departments, IReadOnlyList<AnalyticsGroup> Districts,
    IReadOnlyList<AnalyticsGroup> Sources, IReadOnlyList<AnalyticsDay> DailyTrend,
    int FlaggedClosureCases, int ClosureFlagCount, IReadOnlyList<AnalyticsFlag> ClosureFlags);

public class AnalyticsService(WorkspaceDb db)
{
    public async Task<AnalyticsSnapshot> LoadAsync(Guid owner, AnalyticsFilter filter, DateTime nowUtc, CancellationToken cancellation = default)
    {
        filter = ValidateFilter(filter);
        nowUtc = Utc(nowUtc);
        var query = db.Grievances.AsNoTracking().Where(g => g.OwnerId == owner && g.CreatedUtc <= nowUtc);
        if (!filter.IncludeSamples) query = query.Where(g => !g.IsSample);
        if (StartUtc(filter, nowUtc) is DateTime start) query = query.Where(g => g.CreatedUtc >= start);
        if (filter.District is not null) query = query.Where(g => g.District == filter.District);
        if (filter.Department is not null) query = query.Where(g => g.Department == filter.Department);
        var records = await query.ToListAsync(cancellation);
        return Summarize(records, filter, nowUtc);
    }

    public async Task<string[]> DistrictOptionsAsync(Guid owner, bool includeSamples, CancellationToken cancellation = default)
    {
        var query = db.Grievances.AsNoTracking().Where(g => g.OwnerId == owner);
        if (!includeSamples) query = query.Where(g => !g.IsSample);
        return await query.Where(g => g.District != "").Select(g => g.District).Distinct().OrderBy(d => d).ToArrayAsync(cancellation);
    }

    public static AnalyticsSnapshot Summarize(IEnumerable<Grievance> records, AnalyticsFilter filter, DateTime nowUtc)
    {
        filter = ValidateFilter(filter);
        nowUtc = Utc(nowUtc);
        var start = StartUtc(filter, nowUtc);
        var rows = records.Where(g => (filter.IncludeSamples || !g.IsSample)
            && g.CreatedUtc <= nowUtc && (start is null || g.CreatedUtc >= start)
            && (filter.District is null || g.District == filter.District)
            && (filter.Department is null || g.Department == filter.Department))
            .OrderByDescending(g => g.CreatedUtc).ThenBy(g => g.Reference, StringComparer.Ordinal).ToArray();
        var closed = rows.Count(g => g.Status == "Closed");
        var durations = rows.Where(g => g.Status == "Closed" && g.ClosedUtc.HasValue
                && g.ClosedUtc.Value >= g.CreatedUtc && g.ClosedUtc.Value <= nowUtc)
            .Select(g => (g.ClosedUtc!.Value - g.CreatedUtc).TotalHours).ToArray();
        var assessments = rows.Where(g => g.Status == "ClosureReview")
            .Select(g => CaseRules.AssessClosure(g.OriginalText, g.ResolutionReport, g.EvidenceReference, g.CitizenFeedback)).ToArray();
        var flags = assessments.SelectMany(a => a.Flags).GroupBy(f => f, StringComparer.Ordinal)
            .Select(g => new AnalyticsFlag(g.Key, g.Count())).OrderByDescending(f => f.Count).ThenBy(f => f.Label, StringComparer.Ordinal).ToArray();
        var trend = rows.GroupBy(g => g.CreatedUtc.Date).OrderBy(g => g.Key)
            .Select(g => new AnalyticsDay(DateTime.SpecifyKind(g.Key, DateTimeKind.Utc), g.Count())).ToArray();
        return new(rows, rows.Length, rows.Length - closed, closed,
            durations.Length == 0 ? null : durations.Average(), durations.Length, rows.Count(g => g.IsSample),
            Group(rows, g => DepartmentLabel(g.Department)), Group(rows, g => DisplayLabel(g.District)),
            Group(rows, g => DisplayLabel(g.Source)), trend,
            assessments.Count(a => !a.CanClose), assessments.Sum(a => a.Flags.Count), flags);
    }

    public static string CsvCell(string? value)
    {
        value ??= "";
        var significant = value.TrimStart();
        var leading = value.AsSpan(0, value.Length - significant.Length);
        if ((significant.Length > 0 && significant[0] is '=' or '+' or '-' or '@')
            || leading.IndexOfAny('\t', '\r', '\n') >= 0)
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string ExportCsv(AnalyticsSnapshot snapshot)
    {
        var result = new StringBuilder("Reference,Title,District,Department,Status,CreatedUtc,ClosedUtc,IsSample\r\n");
        foreach (var row in snapshot.Rows)
        {
            var cells = new[] { row.Reference, row.Title, row.District, DepartmentLabel(row.Department),
                CaseRules.StatusLabel(row.Status), Utc(row.CreatedUtc).ToString("O", CultureInfo.InvariantCulture),
                row.ClosedUtc.HasValue ? Utc(row.ClosedUtc.Value).ToString("O", CultureInfo.InvariantCulture) : "",
                row.IsSample ? "true" : "false" };
            result.AppendJoin(',', cells.Select(CsvCell)).Append("\r\n");
        }
        return result.ToString();
    }

    private static AnalyticsFilter ValidateFilter(AnalyticsFilter filter)
    {
        if (filter.Period is not ("7" or "30" or "90" or "all"))
            throw new ArgumentException("Choose the last 7, 30 or 90 days, or all time.");
        var district = string.IsNullOrWhiteSpace(filter.District) ? null : filter.District.Trim();
        var department = string.IsNullOrWhiteSpace(filter.Department) ? null : filter.Department.Trim();
        if (district is { Length: > 100 }) throw new ArgumentException("District filters must be 100 characters or fewer.");
        if (department is not null && !CaseRules.Departments.ContainsKey(department))
            throw new ArgumentException("Choose a supported service filter.");
        return filter with { District = district, Department = department };
    }

    private static DateTime? StartUtc(AnalyticsFilter filter, DateTime nowUtc) => filter.Period == "all"
        ? null : nowUtc.Date.AddDays(1 - int.Parse(filter.Period, CultureInfo.InvariantCulture));

    // SQLite returns unspecified DateTime kinds; stored *Utc fields remain UTC.
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string DisplayLabel(string value) => string.IsNullOrWhiteSpace(value) ? "Not recorded" : value;
    private static string DepartmentLabel(string value) => CaseRules.Departments.TryGetValue(value, out var label) ? label : DisplayLabel(value);
    private static AnalyticsGroup[] Group(IEnumerable<Grievance> rows, Func<Grievance, string> label)
        => rows.GroupBy(label, StringComparer.Ordinal).Select(g => new AnalyticsGroup(g.Key, g.Count(),
            g.Count(row => row.Status != "Closed"), g.Count(row => row.Status == "Closed")))
            .OrderByDescending(g => g.Volume).ThenBy(g => g.Label, StringComparer.Ordinal).ToArray();
}
