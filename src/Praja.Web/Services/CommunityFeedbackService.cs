using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;

public record CommunityFeedbackSummary(int Fixed, int NotFixed);
public record CommunityFeedbackReport(Guid Id, string Decision, string Note, DateTime CreatedUtc,
    string ReviewState, string ReviewNote, Guid? ReviewedBy, DateTime? ReviewedUtc, int Version, int FeedbackRound);
public record CommunityFeedbackCase(string Reference);
public record CommunityBrowserFeedback(int CurrentRound, bool HasCurrentReport, string? PreviousDecision, int? PreviousRound);

public partial class CommunityFeedbackService(WorkspaceDb db)
{
    private IQueryable<Grievance> Eligible() => db.Grievances.Where(g => g.PublicMapConsent && !g.IsSample
        && (g.Status == "AwaitingCitizen" || g.Status == "Closed")
        && g.IncidentLatitude.HasValue && g.IncidentLongitude.HasValue
        && g.IncidentLatitude >= -90 && g.IncidentLatitude <= 90
        && g.IncidentLongitude >= -180 && g.IncidentLongitude <= 180
        && db.CaseWorkflows.Any(w => w.Id == g.Id && (w.Stage == "CitizenConfirmation" || w.Stage == "Closed")));

    public Task<CommunityFeedbackCase?> GetEligibleAsync(string reference) => Eligible().AsNoTracking()
        .Where(g => g.Reference == reference).Select(g => new CommunityFeedbackCase(g.Reference)).SingleOrDefaultAsync();

    public async Task<Guid> SubmitAsync(string reference, string browserId, string decision, string? note,
        double latitude, double longitude, double accuracyMetres, int? expectedRound = null)
    {
        if (string.IsNullOrWhiteSpace(browserId) || browserId.Length < 32 || browserId.Length > 200)
            throw new ArgumentException("Your browser verification session expired. Reload the page and try again.");
        if (decision is not ("Fixed" or "NotFixed")) throw new ArgumentException("Choose fixed or not fixed.");
        note = (note ?? "").Trim();
        if (note.Length > 500) throw new ArgumentException("Keep the note to 500 characters or fewer.");
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180
            || !double.IsFinite(accuracyMetres) || accuracyMetres <= 0 || accuracyMetres > 200)
            throw new ArgumentException("A current browser location accurate to 200 metres or better is required.");

        // The transaction keeps consent/eligibility checks and report insertion together.
        // Browser-reported coordinates are only checked here; they are never retained.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var item = await Eligible().AsNoTracking().SingleOrDefaultAsync(g => g.Reference == reference)
            ?? throw new InvalidOperationException("This complaint is not available for community feedback.");
        var work = await db.CaseWorkflows.AsNoTracking().SingleAsync(w => w.Id == item.Id);
        if (expectedRound.HasValue && expectedRound != work.FeedbackRound)
            throw new InvalidOperationException("The repair round changed. Reload this complaint before submitting feedback.");
        if (DistanceMetres(latitude, longitude, item.IncidentLatitude!.Value, item.IncidentLongitude!.Value) > 1000)
            throw new ArgumentException("Please report from within 1 kilometre of the incident location.");
        var hash = BrowserHash(item.Id, browserId);
        if (await db.CommunityVerifications.AnyAsync(r => r.CaseId == item.Id && r.BrowserHash == hash && r.FeedbackRound == work.FeedbackRound))
            throw new InvalidOperationException("This browser has already submitted feedback for this repair round.");
        var report = new CommunityVerification { CaseId = item.Id, BrowserHash = hash, Decision = decision, Note = note, FeedbackRound = work.FeedbackRound };
        db.CommunityVerifications.Add(report);
        var recipients = await (from user in db.Operators
            join grant in db.OfficerRoles on user.Id equals grant.Id into grants
            from grant in grants.DefaultIfEmpty()
            where !user.IsDisabled && (grant == null || grant.Role == "Admin" || grant.Role == "Intake" || user.Id == work.AssignedOfficerId)
            select user.Id).ToListAsync();
        foreach (var recipient in recipients.Distinct())
            db.CaseNotifications.Add(new() { CaseId = item.Id, Recipient = recipient.ToString(), EventKey = "community:" + report.Id,
                Severity = decision == "NotFixed" ? "Review" : "Update",
                Message = "Unverified nearby community report: " + (decision == "Fixed" ? "work appears fixed." : "work appears not fixed; staff review needed.") + " Case status is unchanged." });
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            if (await db.CommunityVerifications.AnyAsync(r => r.CaseId == item.Id && r.BrowserHash == hash && r.FeedbackRound == work.FeedbackRound))
                throw new InvalidOperationException("This browser has already submitted feedback for this repair round.");
            throw;
        }
        return report.Id;
    }

    public async Task<CommunityFeedbackSummary> SummaryAsync(Guid caseId)
    {
        var counts = await db.CommunityVerifications.AsNoTracking().Where(r => r.CaseId == caseId
                && db.CaseWorkflows.Any(w => w.Id == caseId && w.FeedbackRound == r.FeedbackRound))
            .GroupBy(r => r.Decision).Select(g => new { Decision = g.Key, Count = g.Count() }).ToListAsync();
        return new(counts.FirstOrDefault(c => c.Decision == "Fixed")?.Count ?? 0, counts.FirstOrDefault(c => c.Decision == "NotFixed")?.Count ?? 0);
    }

    // Private notes are for authorized staff only; callers must enforce case access.
    public async Task<IReadOnlyList<CommunityFeedbackReport>> ReportsAsync(Guid caseId) => await db.CommunityVerifications.AsNoTracking()
        .Where(r => r.CaseId == caseId).OrderByDescending(r => r.CreatedUtc).Take(100)
        .Select(r => new CommunityFeedbackReport(r.Id, r.Decision, r.Note, r.CreatedUtc,
            r.ReviewState, r.ReviewNote, r.ReviewedBy, r.ReviewedUtc, r.Version, r.FeedbackRound)).ToListAsync();

    public async Task<CommunityBrowserFeedback> GetBrowserFeedbackAsync(Guid caseId, string browserId)
    {
        var round = await db.CaseWorkflows.AsNoTracking().Where(w => w.Id == caseId).Select(w => w.FeedbackRound).SingleAsync();
        if (string.IsNullOrWhiteSpace(browserId) || browserId.Length < 32 || browserId.Length > 200)
            return new(round, false, null, null);
        var hash = BrowserHash(caseId, browserId);
        var reports = db.CommunityVerifications.AsNoTracking().Where(r => r.CaseId == caseId && r.BrowserHash == hash);
        var current = await reports.AnyAsync(r => r.FeedbackRound == round);
        var previous = await reports.Where(r => r.FeedbackRound < round).OrderByDescending(r => r.FeedbackRound)
            .ThenByDescending(r => r.CreatedUtc).Select(r => new { r.Decision, r.FeedbackRound }).FirstOrDefaultAsync();
        return new(round, current, previous?.Decision, previous?.FeedbackRound);
    }

    private static string BrowserHash(Guid caseId, string browserId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(caseId + ":" + browserId)));

    private static double DistanceMetres(double a, double b, double c, double d)
    {
        const double rad = Math.PI / 180;
        var value = Math.Pow(Math.Sin((c - a) * rad / 2), 2) + Math.Cos(a * rad) * Math.Cos(c * rad) * Math.Pow(Math.Sin((d - b) * rad / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(value, 0, 1)));
    }
}

