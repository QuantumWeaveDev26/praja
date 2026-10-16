using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
namespace Praja.Tests;

public class AutomationFlowTests
{
    private const string Water="No water supply in Ward 12, Mysuru, for three days. Please restore supply.";
    [Fact]
    public async Task PreparedSourceNeedsOnlyOneReviewAndApprovalReusesDraftWithoutCallingAiAgain()
    {
        var provider=new Replies();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        Guid id;
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();
            id=(await scope.ServiceProvider.GetRequiredService<InboxService>().CaptureAsync(owner.Id,"Tester",Water,"Email","English",null,"email-test",true)).Id;
        }
        await Post(client,"/Automation?handler=Enable",new(){["Permission"]="true"});await Post(client,"/Automation?handler=Run",new());
        Assert.Equal(2,provider.AiCalls);var page=await client.GetStringAsync("/Automation/Review/"+id);Assert.Contains("value=\"Mysuru\"",page);Assert.Contains("value=\"Ward 12\"",page);
        var fields=Approval();
        var result=await Post(client,"/Automation/Review/"+id+"?handler=Approve",fields);Assert.Equal(HttpStatusCode.Redirect,result.StatusCode);Assert.Equal(2,provider.AiCalls);
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();Assert.Equal("Registered",item.Status);Assert.Equal("water_supply",item.CategoryCode);Assert.Equal(Water,item.OriginalText);Assert.Equal("Converted",(await db.InboxMessages.SingleAsync()).State);}
        var replay=await client.GetAsync("/Automation/Review/"+id);Assert.Equal(HttpStatusCode.Redirect,replay.StatusCode);
        using var check=app.Services.CreateScope();Assert.Single(await check.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllInboxCollectionIgnoresInvoiceLocallyAndDoesNotRequireComplaintLabel(bool oversizedInvoice)
    {
        var provider=new Replies{OversizedInvoice=oversizedInvoice};using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        using(var scope=app.Services.CreateScope())
        {
            var owner=await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync();var settings=scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>();
            settings.Save(owner.Id,"synthetic.apps.googleusercontent.com","synthetic-secret-only-123","team.quantumweaveai@gmail.com","No-Label-Needed");
            settings.Update(owner.Id,s=>s with{AccessToken="synthetic-access-only",RefreshToken="synthetic-refresh-only",VerifiedMailbox=s.Mailbox,AccessExpiresUtc=DateTimeOffset.UtcNow.AddHours(1)});
        }
        await Post(client,"/Automation?handler=Enable",new(){["Permission"]="true"});await Post(client,"/Automation?handler=Run",new());
        Assert.Equal(0,provider.LabelReads);Assert.Contains("in%3Ainbox",provider.MailQuery,StringComparison.OrdinalIgnoreCase);Assert.Equal(2,provider.BodyReads);Assert.Equal(2,provider.AiCalls);Assert.DoesNotContain("subscription invoice",string.Join("",provider.AiBodies));
        await Post(client,"/Automation?handler=Run",new());Assert.Equal(2,provider.BodyReads);Assert.Equal(2,provider.AiCalls);
        using var scopeCheck=app.Services.CreateScope();var dbCheck=scopeCheck.ServiceProvider.GetRequiredService<WorkspaceDb>();Assert.Single(await dbCheck.InboxMessages.ToListAsync());Assert.Empty(await dbCheck.Grievances.ToListAsync());
        var ownerCheck=await dbCheck.Operators.SingleAsync();Assert.Single(scopeCheck.ServiceProvider.GetRequiredService<AutomationStore>().Read(ownerCheck.Id).Prepared);
    }
    [Fact]
    public async Task ChangingTheConnectedMailboxDoesNotReuseAnotherMailboxesAutomationPermission()
    {
        var provider=new Replies();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        await Post(client,"/Automation?handler=Enable",new(){["Permission"]="true"});
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();var settings=scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>();
            settings.Save(owner.Id,"synthetic.apps.googleusercontent.com","synthetic-secret-only-123","different@example.test","Complaint-label");settings.Update(owner.Id,s=>s with{VerifiedMailbox=s.Mailbox,RefreshToken="synthetic-refresh-only",AccessToken="synthetic-access-only",AccessExpiresUtc=DateTimeOffset.UtcNow.AddHours(1)});
        }
        await Post(client,"/Automation?handler=Run",new());Assert.Equal(0,provider.BodyReads);Assert.Equal("",provider.MailQuery);Assert.Equal(0,provider.AiCalls);
    }
    [Fact]
    public async Task MissingDetailsAndBadCategoryCannotCreatePartialCases()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        Guid id;
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();id=(await scope.ServiceProvider.GetRequiredService<InboxService>().CaptureAsync(owner.Id,"Tester","No water supply for three days. Please help.","Email","English",null,"missing",true)).Id;}
        await Post(client,"/Automation?handler=Enable",new(){["Permission"]="true"});await Post(client,"/Automation?handler=Run",new());
        var fields=Approval();fields["District"]="";var bad=await Post(client,"/Automation/Review/"+id+"?handler=Approve",fields);Assert.Contains("district and location",await bad.Content.ReadAsStringAsync());
        fields=Approval();fields["CategoryCode"]="bus_services";bad=await Post(client,"/Automation/Review/"+id+"?handler=Approve",fields);Assert.Contains("belonging",await bad.Content.ReadAsStringAsync());
        using var check=app.Services.CreateScope();var dbCheck=check.ServiceProvider.GetRequiredService<WorkspaceDb>();Assert.Empty(await dbCheck.Grievances.ToListAsync());Assert.Equal("Pending",(await dbCheck.InboxMessages.SingleAsync()).State);
    }
    [Fact]
    public async Task PermissionPauseCsrfAndOwnershipAreEnforcedAndQueueIsEncrypted()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync("/Automation")).StatusCode);await Setup(client);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/Automation?handler=Enable",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        await Post(client,"/Automation?handler=Enable",new());using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();Assert.False(scope.ServiceProvider.GetRequiredService<AutomationStore>().Read(owner.Id).Enabled);}
        await Post(client,"/Automation?handler=Demo",new(){["Permission"]="true"});
        var file=Directory.GetFiles(app.SettingsDirectory,"automation-*.protected").Single();Assert.DoesNotContain("Fictional",await File.ReadAllTextAsync(file));
        Guid id;using(var scope=app.Services.CreateScope()){id=(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.FirstAsync()).Id;}
        using var other=new TestApp();using var otherClient=other.CreateClient(new(){AllowAutoRedirect=false});await Setup(otherClient);Assert.Equal(HttpStatusCode.NotFound,(await otherClient.GetAsync("/Automation/Review/"+id)).StatusCode);
        await Post(client,"/Automation?handler=Pause",new());await Post(client,"/Automation?handler=Run",new());
        using var check=app.Services.CreateScope();var checkDb=check.ServiceProvider.GetRequiredService<WorkspaceDb>();var account=await checkDb.Operators.SingleAsync();Assert.False(check.ServiceProvider.GetRequiredService<AutomationStore>().Read(account.Id).Enabled);
    }
    [Fact]
    public async Task SocialDemoHasThreePreparedSourcesAndRetriesNeverDuplicateThem()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        await Post(client,"/Automation?handler=Demo",new(){["Permission"]="true"});await Post(client,"/Automation?handler=Demo",new(){["Permission"]="true"});
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();var state=scope.ServiceProvider.GetRequiredService<AutomationStore>().Read(owner.Id);
        Assert.Equal(3,await db.InboxMessages.CountAsync());Assert.Equal(3,state.Prepared.Length);Assert.All(state.Prepared,p=>Assert.True(p.IsDemo));Assert.Empty(await db.Grievances.ToListAsync());
        var proposal=state.Prepared.Single(p=>p.Source.StartsWith("X"));var result=await Post(client,"/Automation/Review/"+proposal.InboxId+"?handler=Approve",Approval());Assert.Equal(HttpStatusCode.Redirect,result.StatusCode);
        Assert.True((await db.Grievances.AsNoTracking().SingleAsync()).IsSample);
    }
    [Theory]
    [InlineData("Your monthly subscription invoice is available. Thank you for your purchase.",false)]
    [InlineData("Good morning Mysuru. Have a wonderful day!",false)]
    [InlineData("No water supply in Ward 12, Mysuru. Please restore supply.",true)]
    [InlineData("My pension has not received a response; this is an unresolved grievance.",true)]
    [InlineData("ಮೈಸೂರು ವಾರ್ಡ್ 12 ಶಾಲೆಯಲ್ಲಿ ಶಿಕ್ಷಕರಿಲ್ಲ. ದಯವಿಟ್ಟು ಶಿಕ್ಷಕರನ್ನು ನೇಮಿಸಿ.",true)]
    public void DetectorSeparatesBasicFixtures(string text,bool expected)=>Assert.Equal(expected,AutomationService.LooksLikeComplaint(text));
    [Fact]
    public void LocalExtractionPreservesQuotesAndNeverChoosesBetweenTwoLocations()
    {
        var local=new IntakeAnalysis("","","","","Unknown",Praja.Web.Domain.CaseRules.SuggestDepartment(Water),"Local",false);
        var one=AutomationService.WithLocalQuotes(Water,local);Assert.Equal("Mysuru",one.District);Assert.Equal("Ward 12",one.Location);
        var mixed=AutomationService.WithLocalQuotes(Water+" Also Ward 13 in Dharwad.",local);Assert.Equal("",mixed.District);Assert.Equal("",mixed.Location);
    }
    private static Dictionary<string,string> Approval()=>new(){["Version"]="1",["Title"]="Water supply complaint",["DraftText"]=Water,["District"]="Mysuru",["Location"]="Ward 12",["Department"]="water",["CategoryCode"]="water_supply",["SubcategoryCode"]="no_supply",["Reviewed"]="true"};
    private static async Task Setup(HttpClient client)=>Assert.Equal(HttpStatusCode.Redirect,(await Post(client,"/Setup",new(){["Input.Name"]="Automation tester",["Input.Email"]="automation@example.test",["Input.Password"]="Automation-Test-Password-2026!"})).StatusCode);
    private static async Task<HttpResponseMessage> Post(HttpClient client,string path,Dictionary<string,string> fields)
    {var page=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,page.StatusCode);fields["__RequestVerificationToken"]=WebUtility.HtmlDecode(Regex.Match(await page.Content.ReadAsStringAsync(),"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);return await client.PostAsync(path,new FormUrlEncodedContent(fields));}
    private sealed class Replies:HttpMessageHandler
    {
        public bool OversizedInvoice;
        public int AiCalls,BodyReads,LabelReads;public string MailQuery="";public List<string> AiBodies=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            object payload;
            if(request.RequestUri!.Host=="gmail.googleapis.com")
            {
                if(request.RequestUri.AbsolutePath.EndsWith("/messages")){MailQuery=request.RequestUri.Query;payload=new{messages=new[]{new{id="complaint001"},new{id="invoice001"}}};}
                else if(request.RequestUri.AbsolutePath.Contains("/messages/")){BodyReads++;var invoice=request.RequestUri.AbsolutePath.EndsWith("invoice001");payload=new{id=invoice?"invoice001":"complaint001",labelIds=new[]{"INBOX"},payload=new{mimeType="text/plain",headers=new[]{new{name="Subject",value=invoice?"Invoice":"Water complaint"}},body=new{data=Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(invoice?(OversizedInvoice?new string('x',140000):"Your monthly subscription invoice is available. Thank you for your purchase."):Water))}}};}
                else{LabelReads++;return new(HttpStatusCode.BadRequest);}
            }
            else
            {
                AiCalls++;var body=await request.Content!.ReadAsStringAsync(cancellation);AiBodies.Add(body);
                object content=body.Contains("needsRoutingReview")?new{district="Mysuru",location="Ward 12",timeline="three days",requestedAction="Please restore supply",department="water",category="water_supply",subcategory="no_supply",routingEvidence="No water supply",needsRoutingReview=false,language="English"}:new{title="Water supply complaint",draft=Water,summary="Water supply is unavailable."};
                payload=new{choices=new[]{new{message=new{content=JsonSerializer.Serialize(content)}}}};
            }
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json")};
        }
    }
}
