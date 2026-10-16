using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;

namespace Praja.Tests;

public class PublicMapTests
{
    private const string Png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=";
    private static async Task<JsonDocument> Data(HttpClient client)
    {
        var response=await client.GetAsync("/PublicMap?handler=Data");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Contains("no-store",response.Headers.CacheControl?.ToString()??"");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    private static async Task Stage(HttpClient client,string reference,string expected)
    {
        using var data=await Data(client);var item=Assert.Single(data.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(reference,item.GetProperty("reference").GetString());Assert.Equal(expected,item.GetProperty("stage").GetString());
        var history = await client.GetAsync("/PublicHistory?reference="+Uri.EscapeDataString(reference));
        Assert.Equal(HttpStatusCode.OK,history.StatusCode);
        Assert.Contains("no-store",history.Headers.CacheControl?.ToString()??"");
        var html = await history.Content.ReadAsStringAsync();
        Assert.Contains("Current progress: <strong>"+Praja.Web.Domain.CaseRules.WorkflowLabel(expected),html);
        foreach(var secret in new[]{"Private officer", "Private crew", "555001", "555002", "12.3051", "76.6551"})Assert.DoesNotContain(secret,html);
        Assert.Equal(12.31,item.GetProperty("approximateLatitude").GetDouble());Assert.Equal(76.66,item.GetProperty("approximateLongitude").GetDouble());
    }

    [Fact]
    public async Task OptedInComplaintMapTracksContractorOfficerReviewerAndCitizenClosureThenPrivateWithdrawal()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;
        var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var workflow=sp.GetRequiredService<WorkflowService>();
        using var publicClient=app.CreateClient(new(){AllowAutoRedirect=false});
        var submitted=await Post(publicClient,"/Submit",new(){["SubmissionKey"]=Guid.NewGuid().ToString(),["Original"]="No water supply for three days at private household 555001.",["District"]="Mysuru",["Location"]="Private street number 41",["Language"]="English",["Consent"]="true",["Sample"]="false",["Latitude"]="12.3051",["Longitude"]="76.6551",["Accuracy"]="10",["PublicMapConsent"]="true"});
        Assert.Contains("Complaint received",await submitted.Content.ReadAsStringAsync());
        var receipt=await db.CitizenReceipts.SingleAsync();var item=await db.Grievances.SingleAsync(g=>g.Id==receipt.Id);var code=workflow.ReceiptCode(receipt);
        Assert.True(item.PublicMapConsent);await Stage(publicClient,item.Reference,"Registered");
        var registeredAt=await db.CaseNotifications.Where(n=>n.CaseId==item.Id&&n.EventKey=="registered").MinAsync(n=>n.CreatedUtc);
        Assert.Equal(2,await sp.GetRequiredService<NotificationService>().CheckDeadlinesAsync(registeredAt.AddDays(3))); // Seed case and submitted case both await assignment.
        Assert.True(await db.CaseNotifications.AnyAsync(n=>n.CaseId==item.Id&&n.EventKey=="unassigned:3"&&n.Recipient=="citizen"));
        Assert.Equal(HttpStatusCode.Redirect,(await Post(publicClient,"/Track?handler=Open",new(){["Code"]=code})).StatusCode);
        Assert.Contains("Awaiting first department assignment",await publicClient.GetStringAsync("/Track"));
        await workflow.CreateOfficerAsync(ids.Admin,"Private crew name","crew-map@example.test","Synthetic-password-123","Contractor","water");
        var crew=(await db.Operators.SingleAsync(o=>o.Email=="crew-map@example.test")).Id;
        await workflow.AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(3),item.Version);var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==item.Id);
        await Stage(publicClient,item.Reference,"Assigned");
        await workflow.ActAsync(ids.Officer,item.Id,work.Version,"Accept","Private officer acceptance note");
        await workflow.AssignContractorAsync(ids.Officer,item.Id,crew,work.Version,"Private contractor assignment reason");
        await workflow.ActAsync(crew,item.Id,work.Version,"FieldAccept","Private field acceptance note");
        await workflow.ActAsync(crew,item.Id,work.Version,"FieldProgress","Private progress note with contact 555002");
        await Stage(publicClient,item.Reference,"InProgress");
        var bytes=Convert.FromBase64String(Png);using var stream=new MemoryStream(bytes);
        var photo=await sp.GetRequiredService<EvidenceService>().UploadAsync(item.Id,item.OwnerId,"Private crew name",item.Version,new FormFile(stream,0,bytes.Length,"Upload","private-completion.png"),"Private completion evidence",true);
        await workflow.ActAsync(crew,item.Id,work.Version,"FieldComplete","Private repair note water restored",photo.Id,12.3051,76.6551,DateTime.UtcNow,true);
        await Stage(publicClient,item.Reference,"OfficerReview");
        await workflow.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Private department inspection note",checkedEvidence:true);await Stage(publicClient,item.Reference,"Verification");
        await workflow.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Private reviewer inspection note",checkedEvidence:true);await Stage(publicClient,item.Reference,"CitizenConfirmation");
        await workflow.RespondAsync(code,work.Version,true,"Private citizen approval feedback");await Stage(publicClient,item.Reference,"Closed");
        using(var snapshot=await Data(publicClient)){
            var raw=snapshot.RootElement.GetRawText();
            foreach(var secret in new[]{"Private","private","555001","555002","12.3051","76.6551",item.Id.ToString(),ids.Officer.ToString(),photo.Id.ToString(),code})Assert.DoesNotContain(secret,raw);
        }
        using var stranger=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(HttpStatusCode.NotFound,(await Post(stranger,"/Track?handler=HideMap",new())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await stranger.PostAsync("/Track?handler=HideMap",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await Post(publicClient,"/Track?handler=Open",new(){["Code"]=code})).StatusCode);
        Assert.Contains("Remove from public map",await publicClient.GetStringAsync("/Track"));
        Assert.Contains("Awaiting first department assignment",await publicClient.GetStringAsync("/Track"));
        var previousWorkVersion=work.Version;var previousItemVersion=item.Version;
        Assert.Equal(HttpStatusCode.Redirect,(await Post(publicClient,"/Track?handler=HideMap",new())).StatusCode);
        using(var hidden=await Data(publicClient))Assert.Empty(hidden.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,(await publicClient.GetAsync("/PublicHistory?reference="+item.Reference)).StatusCode);
        await db.Entry(item).ReloadAsync();await db.Entry(work).ReloadAsync();Assert.False(item.PublicMapConsent);Assert.Equal("Closed",work.Stage);Assert.Equal(previousWorkVersion,work.Version);Assert.Equal(previousItemVersion+1,item.Version);
        Assert.DoesNotContain("Remove from public map",await publicClient.GetStringAsync("/Track"));
        Assert.Equal(HttpStatusCode.Redirect,(await Post(publicClient,"/Track?handler=HideMap",new())).StatusCode);
        await db.Entry(item).ReloadAsync();Assert.Equal(previousItemVersion+1,item.Version);
    }

    [Fact]
    public async Task ProjectionExcludesPrivateSamplesDraftsAndInvalidLocationsAndWhitelistsOnlyPublicFields()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var ids=await Seed(scope.ServiceProvider);
        Grievance Add(string reference,bool consent=true,bool sample=false,string status="Registered",double? lat=12.345,double? lon=-76.655){var item=new Grievance{OwnerId=ids.Admin,Reference=reference,PublicMapConsent=consent,IsSample=sample,Status=status,IncidentLatitude=lat,IncidentLongitude=lon,Title="Secret title",OriginalText="Secret original",District="Secret district",Location="Secret location",Department="water",Summary="Secret summary",ResolutionReport="Secret report"};db.Grievances.Add(item);return item;}
        Add("VISIBLE");Add("PRIVATE",false);Add("SAMPLE",sample:true);Add("DRAFT",status:"Draft");Add("MISSING",lat:null);Add("LAT-INVALID",lat:91);Add("LON-INVALID",lon:-181);Add("UNKNOWN",status:"Unknown");await db.SaveChangesAsync();
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});using var data=await Data(client);var row=Assert.Single(data.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("VISIBLE",row.GetProperty("reference").GetString());Assert.Equal(12.35,row.GetProperty("approximateLatitude").GetDouble());Assert.Equal(-76.66,row.GetProperty("approximateLongitude").GetDouble());
        Assert.Equal(new[]{"approximateLatitude","approximateLongitude","createdDate","department","departmentLabel","dueDate","reference","stage","stageLabel","updatedDate"},row.EnumerateObject().Select(p=>p.Name).OrderBy(n=>n).ToArray());
        Assert.DoesNotContain("Secret",data.RootElement.GetRawText());Assert.Equal(1,data.RootElement.GetProperty("totalMatching").GetInt32());Assert.False(data.RootElement.GetProperty("truncated").GetBoolean());
        foreach(var reference in new[]{"PRIVATE","SAMPLE","DRAFT","MISSING","LAT-INVALID","LON-INVALID","UNKNOWN","NOT-FOUND"})
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/PublicHistory?reference="+reference)).StatusCode);
        Assert.Contains("No workflow stage changes recorded yet",await client.GetStringAsync("/PublicHistory?reference=VISIBLE"));
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/PublicMap")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync("/Work")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/cases")).StatusCode);
    }

    [Fact]
    public async Task PublicFeedIsBoundedAndClearlyReportsTruncation()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var ids=await Seed(scope.ServiceProvider);var now=DateTime.UtcNow;
        for(var i=0;i<503;i++)db.Grievances.Add(new(){OwnerId=ids.Admin,Reference=$"MAP-{i:D4}",PublicMapConsent=true,Status="Registered",IncidentLatitude=12,IncidentLongitude=76,Department="water",CreatedUtc=now.AddMinutes(-i)});
        await db.SaveChangesAsync();using var client=app.CreateClient(new(){AllowAutoRedirect=false});using var data=await Data(client);
        var rows=data.RootElement.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(500,rows.Length);Assert.Equal(503,data.RootElement.GetProperty("totalMatching").GetInt32());Assert.True(data.RootElement.GetProperty("truncated").GetBoolean());Assert.Equal("MAP-0000",rows[0].GetProperty("reference").GetString());Assert.Equal("MAP-0499",rows[^1].GetProperty("reference").GetString());
    }

    [Fact]
    public async Task ConsentRequiresCoordinatesAndSubmissionReplayCannotPublishPrivateComplaint()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();await Seed(scope.ServiceProvider);var service=scope.ServiceProvider.GetRequiredService<PublicComplaintService>();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SubmitAsync(Guid.NewGuid(),"No water supply in our locality for three days.","Mysuru","Ward 12","English",true,false,publicMapConsent:true));
        var key=Guid.NewGuid();var first=await service.SubmitAsync(key,"No water supply in our locality for three days.","Mysuru","Ward 12","English",true,false,latitude:12.3,longitude:76.6);
        var replay=await service.SubmitAsync(key,"No water supply in our locality for three days.","Mysuru","Ward 12","English",true,false,latitude:12.3,longitude:76.6,publicMapConsent:true);
        Assert.Equal(first.Case.Id,replay.Case.Id);Assert.False(replay.Case.PublicMapConsent);
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});using var data=await Data(client);Assert.Empty(data.RootElement.GetProperty("items").EnumerateArray());
    }
}
