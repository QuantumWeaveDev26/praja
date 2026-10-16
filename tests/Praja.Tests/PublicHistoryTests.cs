using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;

namespace Praja.Tests;

public class PublicHistoryTests
{
    [Fact]
    public async Task HistoryIsBoundedChronologicalAndExcludesUnknownStagesAndPrivateText()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;
        await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();
        item.PublicMapConsent=true;item.IncidentLatitude=12;item.IncidentLongitude=76;
        db.CaseWorkflows.Add(new(){Id=item.Id,AssignedOfficerId=item.OwnerId,Stage="InProgress",DueUtc=DateTime.UtcNow.AddDays(2)});
        await db.SaveChangesAsync();
        var start=DateTime.UtcNow.AddDays(-2);
        for(var i=0;i<205;i++)db.WorkflowUpdates.Add(new(){CaseId=item.Id,Stage=i%2==0?"InProgress":"Verification",AtUtc=start.AddMinutes(i),Actor="Secret identity",Note="Secret notes"});
        db.WorkflowUpdates.Add(new(){CaseId=item.Id,Stage="Secret stage",Actor="Secret identity",Note="Secret note"});
        await db.SaveChangesAsync();
        var map=sp.GetRequiredService<PublicMapService>();var history=await map.HistoryAsync(item.Reference);
        Assert.NotNull(history);Assert.True(history.Truncated);Assert.Equal(200,history.Events.Count);
        Assert.Equal(start.AddMinutes(5),history.Events[0].AtUtc);Assert.Equal(start.AddMinutes(204),history.Events[^1].AtUtc);
        using var client=app.CreateClient();var html=await client.GetStringAsync("/PublicHistory?reference="+item.Reference);
        Assert.Contains("latest 200",html);Assert.DoesNotContain("Secret",html);Assert.DoesNotContain("Check completed work",html);
        Assert.Null(await map.HistoryAsync(null));Assert.Null(await map.HistoryAsync(new string('x',31)));
    }
}
