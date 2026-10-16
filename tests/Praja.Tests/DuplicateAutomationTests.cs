using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
namespace Praja.Tests;
public partial class GmailFlowTests
{
    [Fact]
    public async Task SeparateDuplicateDecisionInvalidatesAndRebuildsPreparedDraft()
    {
        var provider=new GoogleReply{MessageLabel="INBOX"};
        using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        await Setup(client);await Save(client);await FinishConnect(client);
        await Post(client,"/Automation?handler=Enable",new(){["Permission"]="true"});await Post(client,"/Automation?handler=Run",new());
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var item=await db.InboxMessages.SingleAsync();
        await scope.ServiceProvider.GetRequiredService<InboxService>().ReviewDuplicateAsync(item.Id,item.OwnerId,item.Version,null,"Different incident after the earlier repair was completed.","Reviewer");
        var stale=await client.GetAsync("/Automation/Review/"+item.Id);Assert.Equal(System.Net.HttpStatusCode.Redirect,stale.StatusCode);
        await Post(client,"/Automation?handler=Run",new());
        var proposal=Assert.Single(scope.ServiceProvider.GetRequiredService<AutomationStore>().Read(item.OwnerId).Prepared);
        Assert.Equal(item.Version,proposal.InboxVersion);Assert.Empty(await db.Grievances.ToListAsync());
        Assert.Equal(System.Net.HttpStatusCode.OK,(await client.GetAsync("/Automation/Review/"+item.Id)).StatusCode);
    }
}
