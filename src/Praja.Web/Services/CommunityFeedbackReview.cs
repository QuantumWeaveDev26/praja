using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;

public partial class CommunityFeedbackService
{
    public async Task ReviewAsync(Guid actor, Guid caseId, Guid reportId, int reportVersion,
        int workVersion, string action, string note, DateTime? due = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var account = await db.Operators.AsNoTracking().SingleOrDefaultAsync(o => o.Id == actor);
        if (account is null || account.IsDisabled) throw new UnauthorizedAccessException();
        var role = await db.OfficerRoles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == actor)
            ?? new OfficerRole { Id = actor, Role = account.BootstrapSlot == 1 ? "Admin" : "Intake" };
        var item = await db.Grievances.SingleOrDefaultAsync(g => g.Id == caseId)
            ?? throw new InvalidOperationException("Complaint not found.");
        var work = await db.CaseWorkflows.SingleOrDefaultAsync(w => w.Id == caseId)
            ?? throw new InvalidOperationException("This complaint has no assigned workflow.");
        if (role.Role != "Admin" && !(role.Role == "Department" && work.AssignedOfficerId == actor && role.Department == item.Department))
            throw new UnauthorizedAccessException();
        var report = await db.CommunityVerifications.SingleOrDefaultAsync(r => r.Id == reportId && r.CaseId == caseId)
            ?? throw new InvalidOperationException("Community report not found.");
        if (report.Version != reportVersion || work.Version != workVersion)
            throw new InvalidOperationException("The report or complaint changed. Refresh before continuing.");
        if (report.FeedbackRound != work.FeedbackRound)
            throw new InvalidOperationException("This report belongs to an earlier repair round and is retained as history.");
        note = (note ?? "").Trim();
        if (note.Length is < 5 or > 1000) throw new InvalidOperationException("Write a private review reason between 5 and 1,000 characters.");
        if (report.Decision != "NotFixed") throw new InvalidOperationException("Only not-fixed reports require this review workflow.");
        var now = DateTime.UtcNow;
        string message;
        if (action == "Investigate" && report.ReviewState == "Pending")
        {
            report.ReviewState = "Investigating";
            message = "Staff started investigating an unverified community not-fixed report. Complaint status is unchanged.";
        }
        else if (action == "Dismiss" && report.ReviewState is "Pending" or "Investigating")
        {
            report.ReviewState = "Dismissed";
            message = "Staff reviewed and dismissed a community not-fixed report. Complaint status is unchanged.";
        }
        else if (action == "Reopen" && report.ReviewState == "Investigating")
        {
            if (work.Stage is not ("CitizenConfirmation" or "Closed") || item.Status is not ("AwaitingCitizen" or "Closed"))
                throw new InvalidOperationException("Only work awaiting citizen confirmation or already closed can be reopened.");
            if (due is null || due.Value.Date < now.Date || due.Value.Date > now.Date.AddYears(1))
                throw new InvalidOperationException("Choose a new target date from today through the next year.");
            // Audit entries are staff-only. Preserve prior completion metadata before reset;
            // citizen-visible workflow messages contain no coordinates or private findings.
            db.AuditEntries.Add(new() { GrievanceId = caseId, Actor = account.Name, AtUtc = now,
                Action = FormattableString.Invariant($"Community reopening snapshot: photo={work.CompletionPhotoId}; latitude={work.Latitude}; longitude={work.Longitude}; captured={work.CapturedUtc:O}; completedBy={work.CompletedBy}; verifiedBy={work.VerifiedBy}; previousDue={work.DueUtc:O}.") });
            report.ReviewState = "Reopened";
            work.Stage = "InProgress";
            work.DueUtc = DateTime.SpecifyKind(due.Value.Date, DateTimeKind.Utc);
            work.ReopenedUtc = now;
            work.FeedbackRound++;
            work.CompletionPhotoId = null;
            work.Latitude = null;
            work.Longitude = null;
            work.CapturedUtc = null;
            work.CompletionNote = "";
            work.CompletedBy = null;
            work.VerifiedBy = null;
            work.ContractorAccepted = false;
            item.Status = "InProgress";
            item.ClosedUtc = null;
            item.CitizenFeedback = "Pending";
            item.ResolutionReport = "";
            item.EvidenceReference = "";
            message = "Staff confirmed further work is required after investigating a community report and reopened the complaint. Previous evidence is retained in the case history. New completion evidence and verification are required.";
        }
        else throw new InvalidOperationException("This review action is not available. Refresh the report.");

        report.ReviewNote = note;
        report.ReviewedBy = actor;
        report.ReviewedUtc = now;
        report.Version++;
        work.Version++;
        item.Version++;
        item.UpdatedUtc = now;
        db.WorkflowUpdates.Add(new() { CaseId = caseId, Stage = work.Stage, Actor = account.Name, Note = message, AtUtc = now });
        db.AuditEntries.Add(new() { GrievanceId = caseId, Actor = account.Name, Action = message, AtUtc = now });
        for (var offset = 0; offset < note.Length; offset += 400)
            db.AuditEntries.Add(new() { GrievanceId = caseId, Actor = account.Name, AtUtc = now,
                Action = $"Private community review {report.Id} {action} part {offset / 400 + 1}: " + note.Substring(offset, Math.Min(400, note.Length - offset)) });
        await new NotificationService(db).QueueAsync(item, work, $"community-review:{report.Id}:{report.Version}", message,
            action == "Reopen" ? "Review" : "Update", now);
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("The report or complaint changed in another session. Refresh and retry.");
        }
    }
}
