using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class UnassignedEscalationTests
{
    [Theory]
    [InlineData(23.99, 0, "")]
    [InlineData(24, 1, "Reminder")]
    [InlineData(71.99, 1, "Reminder")]
    [InlineData(72, 1, "Escalation")]
    [InlineData(167.99, 1, "Escalation")]
    [InlineData(168, 1, "Urgent")]
    public async Task ThresholdsUseRegistrationNotDraftAgeAndRestartDoesNotRepeat(double hours, int expected, string severity)
    {
        using var app=new TestApp();var start=DateTime.UtcNow;Guid id;
        using(var scope=app.Services.CreateScope())
        {
            var sp=scope.ServiceProvider;var ids=await TeamWorkflowTests.Seed(sp);id=ids.Case;
            var db=sp.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();item.CreatedUtc=start.AddDays(-50);
            var service=sp.GetRequiredService<NotificationService>();await service.QueueAsync(item,null,"registered","Registered",at:start);await db.SaveChangesAsync();
            Assert.Equal(expected,await service.CheckDeadlinesAsync(start.AddHours(hours)));
            var alerts=await db.CaseNotifications.Where(n=>n.EventKey.StartsWith("unassigned:")).ToListAsync();
            Assert.Equal(expected*2,alerts.Count); // Admin and private citizen, not unrelated officers/reviewer.
            Assert.All(alerts,n=>Assert.Equal(severity,n.Severity));
            Assert.Empty(await service.ForOfficer(ids.Officer).ToListAsync());Assert.Empty(await service.ForOfficer(ids.Reviewer).ToListAsync());
            Assert.Equal(expected,await db.AuditEntries.CountAsync(a=>a.Actor=="Assignment monitor"));
            Assert.Empty(await db.CaseWorkflows.ToListAsync());Assert.Equal("Registered",item.Status);
        }
        using var restarted=app.Services.CreateScope();
        Assert.Equal(0,await restarted.ServiceProvider.GetRequiredService<NotificationService>().CheckDeadlinesAsync(start.AddHours(hours)));
    }

    [Fact]
    public async Task LegacyMilestonesAdvanceOnceAndStopAfterAssignment()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;
        var ids=await TeamWorkflowTests.Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();var start=DateTime.UtcNow.AddDays(-8);item.CreatedUtc=start;await db.SaveChangesAsync();
        var service=sp.GetRequiredService<NotificationService>();
        foreach(var days in new[]{1,3,7})Assert.Equal(1,await service.CheckDeadlinesAsync(start.AddDays(days)));
        Assert.Equal(0,await service.CheckDeadlinesAsync(start.AddDays(2))); // A clock correction cannot send a lower milestone.
        Assert.Equal(0,await service.CheckDeadlinesAsync(start.AddDays(30)));
        var admin=await db.Operators.SingleAsync(o=>o.Id==ids.Admin);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");await db.SaveChangesAsync();
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await TeamWorkflowTests.Post(client,"/Account/Login",new(){["Email"]="admin@example.test",["Password"]="Synthetic-password-123"})).StatusCode);
        Assert.Contains("1 open cases with unread escalations",await client.GetStringAsync("/Work"));
        Assert.Contains("Review unassigned complaints",await client.GetStringAsync("/Work/Notifications"));
        await sp.GetRequiredService<WorkflowService>().AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(10),item.Version);
        Assert.Contains("0 open cases with unread escalations",await client.GetStringAsync("/Work"));
        Assert.Equal(0,await service.CheckDeadlinesAsync(DateTime.UtcNow));
        Assert.Equal(6,await db.CaseNotifications.CountAsync(n=>n.EventKey.StartsWith("unassigned:")));
        Assert.Equal("Assigned",(await db.CaseWorkflows.SingleAsync()).Stage);
    }

    [Fact]
    public async Task OnlyRegisteredUnassignedCasesQualifyAndDowntimeSendsOnlyHighestMilestone()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;
        var ids=await TeamWorkflowTests.Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var now=DateTime.UtcNow;
        var item=await db.Grievances.SingleAsync();item.CreatedUtc=now.AddDays(-10);
        foreach(var state in new[]{"Draft","InProgress","ClosureReview","AwaitingCitizen","Closed"})
            db.Grievances.Add(new(){OwnerId=ids.Admin,Reference=state,Status=state,CreatedUtc=now.AddDays(-50)});
        await db.SaveChangesAsync();var service=sp.GetRequiredService<NotificationService>();Assert.Equal(1,await service.CheckDeadlinesAsync(now));
        Assert.All(await db.CaseNotifications.ToListAsync(),n=>{Assert.Equal(ids.Case,n.CaseId);Assert.Equal("unassigned:7",n.EventKey);});
        Assert.Equal(0,await service.CheckDeadlinesAsync(now.AddDays(1)));
    }

    [Fact]
    public async Task StorageFailureAndStaleVersionRollBackAndRetryWithoutDuplicateAlerts()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;
        var ids=await TeamWorkflowTests.Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();item.CreatedUtc=DateTime.UtcNow.AddDays(-4);await db.SaveChangesAsync();
        var service=sp.GetRequiredService<NotificationService>();
        using(var other=app.Services.CreateScope())
        {
            var changed=other.ServiceProvider.GetRequiredService<WorkspaceDb>();var row=await changed.Grievances.SingleAsync();row.Version++;await changed.SaveChangesAsync();
        }
        Assert.Equal(0,await service.CheckDeadlinesAsync(DateTime.UtcNow));Assert.Empty(await db.CaseNotifications.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailUnassigned BEFORE INSERT ON CaseNotifications BEGIN SELECT RAISE(ABORT, 'test storage failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(()=>service.CheckDeadlinesAsync(DateTime.UtcNow));
        Assert.False(await db.AuditEntries.AnyAsync(a=>a.Actor=="Assignment monitor"));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailUnassigned;");
        Assert.Equal(1,await service.CheckDeadlinesAsync(DateTime.UtcNow));Assert.Equal(0,await service.CheckDeadlinesAsync(DateTime.UtcNow));
    }
}
