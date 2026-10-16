using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;
namespace Praja.Web.Services;

public class NotificationService(WorkspaceDb db)
{
    // Enqueued in the caller's SaveChanges transaction alongside the case change.
    public async Task QueueAsync(Grievance item, CaseWorkflow? work, string key, string message, string severity = "Update", DateTime? at = null)
    {
        var notifyReviewers = work?.Stage == "Verification";
        var recipients = await (from user in db.Operators
            join grant in db.OfficerRoles on user.Id equals grant.Id into grants
            from grant in grants.DefaultIfEmpty()
            where !user.IsDisabled && (grant == null || grant.Role == "Admin" || grant.Role == "Intake" ||
                  (grant.Role == "Reviewer" && notifyReviewers))
            select user.Id).ToListAsync();
        if (work != null && await db.Operators.AnyAsync(o=>o.Id==work.AssignedOfficerId&&!o.IsDisabled)) recipients.Add(work.AssignedOfficerId);
        if (work?.ContractorId is Guid contractor && await db.Operators.AnyAsync(o=>o.Id==contractor&&!o.IsDisabled)) recipients.Add(contractor);
        foreach (var recipient in recipients.Select(id => id.ToString()).Append("citizen").Distinct())
            db.CaseNotifications.Add(new() { CaseId = item.Id, Recipient = recipient, EventKey = key,
                Message = message, Severity = severity, CreatedUtc = at ?? DateTime.UtcNow });
    }

    public IQueryable<CaseNotification> ForOfficer(Guid actor) => db.CaseNotifications.Where(n => n.Recipient == actor.ToString() &&
        db.Operators.Any(o => o.Id == actor && !o.IsDisabled) &&
        (!db.OfficerRoles.Any(r => r.Id == actor && (r.Role == "Department" || r.Role == "Contractor")) ||
        db.CaseWorkflows.Any(w => w.Id == n.CaseId &&
            (w.AssignedOfficerId == actor && db.OfficerRoles.Any(r=>r.Id==actor&&r.Role=="Department") ||
             w.ContractorId == actor && db.OfficerRoles.Any(r=>r.Id==actor&&r.Role=="Contractor")) &&
            db.Grievances.Any(g => g.Id == w.Id && db.OfficerRoles.Any(r => r.Id == actor && r.Department == g.Department)))));

    public async Task<bool> MarkReadAsync(Guid actor, long id)
    {
        var notification = await ForOfficer(actor).SingleOrDefaultAsync(n => n.Id == id);
        if (notification == null) return false;
        notification.ReadUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> CheckDeadlinesAsync(DateTime now, CancellationToken cancellation = default)
    {
        var cases = await db.CaseWorkflows.AsNoTracking().Where(work => work.Stage != "Closed" && work.DueUtc <= now.Date)
            .Select(work => work.Id).ToListAsync(cancellation);
        var count = 0;
        foreach (var id in cases)
        {
            var work = await db.CaseWorkflows.SingleAsync(w => w.Id == id, cancellation);
            if (work.Stage == "Closed" || work.DueUtc.Date > now.Date) continue;
            var item = await db.Grievances.SingleAsync(g => g.Id == id, cancellation);
            var days = (now.Date - work.DueUtc.Date).Days;
            var level = days >= 7 ? 7 : days >= 3 ? 3 : days >= 1 ? 1 : 0;
            var key = $"deadline:{work.DueUtc:yyyyMMdd}:{work.Stage}:{level}";
            if (await db.CaseNotifications.AnyAsync(n => n.CaseId == work.Id && n.EventKey == key, cancellation)) continue;
            var severity = level >= 7 ? "Urgent" : level >= 3 ? "Escalation" : "Reminder";
            var message = level == 0 ? "Target date is today." : $"Target date passed {days} day(s) ago.";
            message += $" Stage: {CaseRules.WorkflowLabel(work.Stage)}. " + (work.Stage == "CitizenConfirmation"
                ? "Awaiting citizen response; staff should follow up. Silence never closes the case."
                : level >= 3 ? "Admin/intake intervention required; responsible staff should update the case." : "Responsible staff should update the case.");
            await QueueAsync(item, work, key, message, severity, now);
            // Compete with workflow changes so a concurrent closure cannot commit a stale escalation.
            work.Version++;
            if (level >= 3)
            {
                db.WorkflowUpdates.Add(new() { CaseId = work.Id, Stage = work.Stage, Actor = "Deadline monitor", Note = message, AtUtc = now });
                db.AuditEntries.Add(new() { GrievanceId = work.Id, Actor = "Deadline monitor", Action = severity + ": " + message, AtUtc = now });
            }
            try { await db.SaveChangesAsync(cancellation); count++; }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                // Another process may have committed this same milestone. Never swallow other failures.
                if (!await db.CaseNotifications.AnyAsync(n => n.CaseId == work.Id && n.EventKey == key, cancellation)) throw;
            }
        }
        return count + await CheckUnassignedAsync(now, cancellation);
    }

    private async Task<int> CheckUnassignedAsync(DateTime now, CancellationToken cancellation)
    {
        var ids = await db.Grievances.AsNoTracking().Where(g => g.Status == "Registered"
            && !db.CaseWorkflows.Any(w => w.Id == g.Id)).Select(g => g.Id).ToListAsync(cancellation);
        var count = 0;
        foreach (var id in ids)
        {
            var item = await db.Grievances.SingleAsync(g => g.Id == id, cancellation);
            if (item.Status != "Registered" || await db.CaseWorkflows.AnyAsync(w => w.Id == id, cancellation)) continue;
            // Registration alerts are persisted with registration. Legacy records use creation time.
            var registered = await db.CaseNotifications.Where(n => n.CaseId == id && n.EventKey == "registered")
                .Select(n => (DateTime?)n.CreatedUtc).MinAsync(cancellation) ?? item.CreatedUtc;
            var age = now - registered;
            if (age < TimeSpan.FromDays(1)) continue;
            var level = age >= TimeSpan.FromDays(7) ? 7 : age >= TimeSpan.FromDays(3) ? 3 : 1;
            var key = $"unassigned:{level}";
            if (await db.CaseNotifications.AnyAsync(n => n.CaseId == id &&
                (n.EventKey == key || n.EventKey == "unassigned:7" || (level == 1 && n.EventKey == "unassigned:3")), cancellation)) continue;
            var severity = level == 7 ? "Urgent" : level == 3 ? "Escalation" : "Reminder";
            var message = $"Awaiting first department assignment for at least {level} day(s). Admin/intake must review the route and assign a department officer. No field work has been assigned through this workflow.";
            await QueueAsync(item, null, key, message, severity, now);
            // Assignment also updates the grievance version: only one competing change may commit.
            item.Version++;
            db.AuditEntries.Add(new() { GrievanceId = id, Actor = "Assignment monitor", Action = severity + ": " + message, AtUtc = now });
            try { await db.SaveChangesAsync(cancellation); count++; }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (!await db.CaseNotifications.AnyAsync(n => n.CaseId == id && n.EventKey == key, cancellation)) throw;
            }
        }
        return count;
    }
}

public sealed class DeadlineWorker(IServiceScopeFactory scopes, IWebHostEnvironment environment, TimeProvider clock, ILogger<DeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<NotificationService>().CheckDeadlinesAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Deadline check failed. Pending milestones will be retried next minute."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
