using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using System.Net;
using Xunit;
namespace Praja.Tests;

public class NotificationTests
{
    [Fact]
    public async Task StaleDeadlineSnapshotRollsBackAlertsAndRetriesCurrentStage()
    {
        using var app = new TestApp(); using var first = app.Services.CreateScope();
        var ids = await TeamWorkflowTests.Seed(first.ServiceProvider);
        await first.ServiceProvider.GetRequiredService<WorkflowService>().AssignAsync(ids.Admin, ids.Case, ids.Officer, DateTime.UtcNow.Date, 1);
        using (var changed = app.Services.CreateScope())
            await changed.ServiceProvider.GetRequiredService<WorkflowService>().ActAsync(ids.Officer, ids.Case, 2, "Accept", "Accepted by another session");
        var db = first.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var service = first.ServiceProvider.GetRequiredService<NotificationService>();
        Assert.Equal(0, await service.CheckDeadlinesAsync(DateTime.UtcNow.Date.AddDays(3)));
        Assert.False(await db.CaseNotifications.AnyAsync(n => n.EventKey.StartsWith("deadline:")));
        Assert.False(await db.WorkflowUpdates.AnyAsync(n => n.Actor == "Deadline monitor"));
        Assert.Equal(1, await service.CheckDeadlinesAsync(DateTime.UtcNow.Date.AddDays(3)));
        Assert.All(await db.CaseNotifications.Where(n => n.EventKey.StartsWith("deadline:")).ToListAsync(), n => Assert.Contains(":InProgress:", n.EventKey));
    }

    [Fact]
    public async Task StorageFailureDoesNotLoseEscalationAndRetrySendsOnce()
    {
        using var app = new TestApp(); using var scope = app.Services.CreateScope();
        var ids = await TeamWorkflowTests.Seed(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<WorkflowService>().AssignAsync(ids.Admin, ids.Case, ids.Officer, DateTime.UtcNow.Date, 1);
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var service = scope.ServiceProvider.GetRequiredService<NotificationService>();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailAlert BEFORE INSERT ON CaseNotifications BEGIN SELECT RAISE(ABORT, 'test storage failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.CheckDeadlinesAsync(DateTime.UtcNow.Date.AddDays(3)));
        Assert.False(await db.WorkflowUpdates.AnyAsync(n => n.Actor == "Deadline monitor"));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailAlert;");
        Assert.Equal(1, await service.CheckDeadlinesAsync(DateTime.UtcNow.Date.AddDays(3)));
        Assert.Equal(0, await service.CheckDeadlinesAsync(DateTime.UtcNow.Date.AddDays(3)));
    }

    [Theory]
    [InlineData(-1, 0, "")]
    [InlineData(0, 1, "Reminder")]
    [InlineData(1, 1, "Reminder")]
    [InlineData(2, 1, "Reminder")]
    [InlineData(3, 1, "Escalation")]
    [InlineData(6, 1, "Escalation")]
    [InlineData(7, 1, "Urgent")]
    public async Task DeadlineBoundariesAndRestartDeduplication(int days, int expected, string severity)
    {
        using var app = new TestApp(); Guid id; DateTime due;
        using (var scope = app.Services.CreateScope())
        {
            var ids = await TeamWorkflowTests.Seed(scope.ServiceProvider); id = ids.Case;
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            var workflow = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            due = DateTime.UtcNow.Date.AddDays(2);
            await workflow.AssignAsync(ids.Admin, id, ids.Officer, due, 1);
            var service = scope.ServiceProvider.GetRequiredService<NotificationService>();
            Assert.Equal(expected, await service.CheckDeadlinesAsync(due.AddDays(days).AddHours(12)));
            var alerts = await db.CaseNotifications.Where(n => n.EventKey.StartsWith("deadline:")).ToListAsync();
            Assert.Equal(expected * 3, alerts.Count); // Admin, assigned officer and private citizen.
            Assert.All(alerts, n => Assert.Equal(severity, n.Severity));
            Assert.Equal(days >= 3 ? 1 : 0, await db.WorkflowUpdates.CountAsync(u => u.Actor == "Deadline monitor"));
            Assert.Equal("Assigned", (await db.CaseWorkflows.SingleAsync()).Stage);
        }
        using var restarted = app.Services.CreateScope();
        Assert.Equal(0, await restarted.ServiceProvider.GetRequiredService<NotificationService>().CheckDeadlinesAsync(due.AddDays(days).AddHours(13)));
    }

    [Fact]
    public async Task EscalationsAdvanceOnceAndNeverCloseCitizenPendingCase()
    {
        using var app = new TestApp(); using var scope = app.Services.CreateScope();
        var ids = await TeamWorkflowTests.Seed(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var workflow = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var service = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var due = DateTime.UtcNow.Date;
        await workflow.AssignAsync(ids.Admin, ids.Case, ids.Officer, due, 1);
        foreach (var day in new[] { 0, 1, 3, 7 }) Assert.Equal(1, await service.CheckDeadlinesAsync(due.AddDays(day)));
        Assert.Equal(0, await service.CheckDeadlinesAsync(due.AddDays(20)));
        var work = await db.CaseWorkflows.SingleAsync(); work.Stage = "CitizenConfirmation";
        var item = await db.Grievances.SingleAsync(); item.Status = "AwaitingCitizen"; await db.SaveChangesAsync();
        Assert.Equal(1, await service.CheckDeadlinesAsync(due.AddDays(20)));
        Assert.Equal("CitizenConfirmation", work.Stage); Assert.Null(item.ClosedUtc);
        Assert.Contains(await db.CaseNotifications.ToListAsync(), n => n.Message.Contains("Silence never closes"));
        work.Stage = "Closed"; item.Status = "Closed"; await db.SaveChangesAsync();
        Assert.Equal(0, await service.CheckDeadlinesAsync(due.AddDays(30)));
    }

    [Fact]
    public async Task WorkflowAlertsAreAtomicPrivateAndCannotBeReadByAnotherOfficer()
    {
        using var app = new TestApp(); using var scope = app.Services.CreateScope();
        var ids = await TeamWorkflowTests.Seed(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var workflow = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var service = scope.ServiceProvider.GetRequiredService<NotificationService>();
        await workflow.AssignAsync(ids.Admin, ids.Case, ids.Officer, DateTime.UtcNow.AddDays(2), 1);
        var own = await service.ForOfficer(ids.Officer).SingleAsync();
        Assert.Empty(await service.ForOfficer(ids.Other).ToListAsync());
        Assert.Empty(await service.ForOfficer(ids.Reviewer).ToListAsync());
        Assert.False(await service.MarkReadAsync(ids.Other, own.Id)); Assert.Null(own.ReadUtc);
        Assert.True(await service.MarkReadAsync(ids.Officer, own.Id)); var read = own.ReadUtc;
        Assert.True(await service.MarkReadAsync(ids.Officer, own.Id)); Assert.Equal(read, own.ReadUtc);
        var count = await db.CaseNotifications.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ActAsync(ids.Officer, ids.Case, 1, "Accept", "Accept this case"));
        Assert.Equal(count, await db.CaseNotifications.CountAsync());
        await workflow.ActAsync(ids.Officer, ids.Case, 2, "Accept", "Accept this case");
        Assert.Equal(count + 3, await db.CaseNotifications.CountAsync());
        // A changed department grant must not retain visibility of old alerts.
        (await db.OfficerRoles.SingleAsync(r => r.Id == ids.Officer)).Department = "sanitation"; await db.SaveChangesAsync();
        Assert.Empty(await service.ForOfficer(ids.Officer).ToListAsync());
        Assert.False(await service.MarkReadAsync(ids.Officer, own.Id));
    }

    [Fact]
    public async Task PublicSubmissionNotifiesIntakeAndCitizenOnlyOnce()
    {
        using var app = new TestApp(); using var scope = app.Services.CreateScope();
        var ids = await TeamWorkflowTests.Seed(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<PublicComplaintService>();
        var key = Guid.NewGuid();
        var first = await service.SubmitAsync(key, "Fictional water supply complaint in Ward 12", "Mysuru", "Ward 12", "English", true, true);
        await service.SubmitAsync(key, "Fictional water supply complaint in Ward 12", "Mysuru", "Ward 12", "English", true, true);
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var alerts = await db.CaseNotifications.Where(n => n.CaseId == first.Case.Id).ToListAsync();
        Assert.Equal(2, alerts.Count); Assert.Contains(alerts, n => n.Recipient == ids.Admin.ToString()); Assert.Contains(alerts, n => n.Recipient == "citizen");
    }

    [Fact]
    public async Task NotificationHttpFormsEnforceAuthenticationCsrfAndRole()
    {
        using var app = new TestApp(); long id;
        using (var scope = app.Services.CreateScope())
        {
            var ids = await TeamWorkflowTests.Seed(scope.ServiceProvider);
            await scope.ServiceProvider.GetRequiredService<WorkflowService>().AssignAsync(ids.Admin, ids.Case, ids.Officer, DateTime.UtcNow.AddDays(2), 1);
            id = (await scope.ServiceProvider.GetRequiredService<NotificationService>().ForOfficer(ids.Officer).SingleAsync()).Id;
        }
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Work/Notifications")).StatusCode);
        using var officer = app.CreateClient(new() { AllowAutoRedirect = false });
        await TeamWorkflowTests.Post(officer, "/Account/Login", new() { ["Email"] = "water@example.test", ["Password"] = "Synthetic-password-123" });
        Assert.Contains("Unread", await officer.GetStringAsync("/Work/Notifications"));
        Assert.Equal(HttpStatusCode.BadRequest, (await officer.PostAsync("/Work/Notifications?handler=Read", new FormUrlEncodedContent(new Dictionary<string,string>{{"id",id.ToString()}}))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TeamWorkflowTests.Post(officer, "/Work/Notifications?handler=Check", new())).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TeamWorkflowTests.Post(officer, "/Work/Notifications?handler=Read", new() { ["id"] = id.ToString() })).StatusCode);
        using var other = app.CreateClient(new() { AllowAutoRedirect = false });
        await TeamWorkflowTests.Post(other, "/Account/Login", new() { ["Email"] = "other@example.test", ["Password"] = "Synthetic-password-123" });
        Assert.DoesNotContain("Case update:", await other.GetStringAsync("/Work/Notifications"));
        Assert.Equal(HttpStatusCode.NotFound, (await TeamWorkflowTests.Post(other, "/Work/Notifications?handler=Read", new() { ["id"] = id.ToString() })).StatusCode);
    }
}
