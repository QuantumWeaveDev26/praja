using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;

namespace Praja.Tests;

public class CommunityFeedbackTests
{
    private const string Png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=";
    private static Dictionary<string,string> Fields(string reference,string decision="Fixed")=>new(){["Reference"]=reference,["Decision"]=decision,["Note"]="Private community observation 555012",["Consent"]="true",["Latitude"]="12.3051",["Longitude"]="76.6551",["Accuracy"]="12",["FeedbackRound"]="1"};
    private static string Token(string html)=>WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task<HttpResponseMessage> Feedback(HttpClient client,Dictionary<string,string> fields)
    {
        var html=await client.GetStringAsync("/Community?reference="+Uri.EscapeDataString(fields["Reference"]));
        if(string.IsNullOrEmpty(Token(html)))html=await client.GetStringAsync("/Community?reference="+Uri.EscapeDataString(fields["Reference"]));
        var round=Regex.Match(html,"name=\"FeedbackRound\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        fields["__RequestVerificationToken"]=Token(html);
        if(round!="")fields["FeedbackRound"]=round;
        if(fields["__RequestVerificationToken"]=="")fields["__RequestVerificationToken"]=Token(await client.GetStringAsync("/Submit"));
        Assert.NotEmpty(fields["__RequestVerificationToken"]);
        return await client.PostAsync("/Community",new FormUrlEncodedContent(fields));
    }
    private static async Task<Grievance> Eligible(IServiceProvider sp)
    {
        var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();
        item.PublicMapConsent=true;item.IncidentLatitude=12.3051;item.IncidentLongitude=76.6551;item.Status="AwaitingCitizen";
        db.CaseWorkflows.Add(new(){Id=item.Id,AssignedOfficerId=ids.Officer,Stage="CitizenConfirmation",DueUtc=DateTime.UtcNow.AddDays(2)});
        await db.SaveChangesAsync();return item;
    }

    [Fact]
    public async Task FullRepairFlowAcceptsNearbyFeedbackWithoutReplacingCitizenApprovalOrLeakingNotes()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);
        var db=sp.GetRequiredService<WorkspaceDb>();var workflow=sp.GetRequiredService<WorkflowService>();var community=sp.GetRequiredService<CommunityFeedbackService>();
        var submission=await sp.GetRequiredService<PublicComplaintService>().SubmitAsync(Guid.NewGuid(),"No water supply for three days in Ward 12.","Mysuru","Ward 12","English",true,false,latitude:12.3051,longitude:76.6551,publicMapConsent:true);
        var item=submission.Case;var receipt=await db.CitizenReceipts.SingleAsync(r=>r.Id==item.Id);var code=workflow.ReceiptCode(receipt);
        using var neighbour=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(HttpStatusCode.NotFound,(await neighbour.GetAsync("/Community?reference="+item.Reference)).StatusCode);
        await workflow.AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(3),item.Version);var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==item.Id);
        await workflow.ActAsync(ids.Officer,item.Id,work.Version,"Accept","Officer accepted the water repair");
        await workflow.CreateOfficerAsync(ids.Admin,"Field crew","community-crew@example.test","Synthetic-password-123","Contractor","water");
        var crew=(await db.Operators.SingleAsync(o=>o.Email=="community-crew@example.test")).Id;
        await workflow.AssignContractorAsync(ids.Officer,item.Id,crew,work.Version,"Repair the interrupted water service");
        await workflow.ActAsync(crew,item.Id,work.Version,"FieldAccept","Field crew accepted this repair");
        await workflow.ActAsync(crew,item.Id,work.Version,"FieldProgress","Field crew reached the incident location");
        var bytes=Convert.FromBase64String(Png);using var stream=new MemoryStream(bytes);
        var photo=await sp.GetRequiredService<EvidenceService>().UploadAsync(item.Id,item.OwnerId,"Field crew",item.Version,new FormFile(stream,0,bytes.Length,"Upload","completion.png"),"Completion photograph",true);
        await workflow.ActAsync(crew,item.Id,work.Version,"FieldComplete","Water restored and tested onsite",photo.Id,12.3051,76.6551,DateTime.UtcNow,true);
        await workflow.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Department checked the repair evidence",checkedEvidence:true);
        await workflow.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Reviewer checked the completed water repair",checkedEvidence:true);
        var previousVersion=work.Version;var previousItemVersion=item.Version;
        Assert.Equal(HttpStatusCode.Redirect,(await Feedback(neighbour,Fields(item.Reference))).StatusCode);
        await db.Entry(item).ReloadAsync();await db.Entry(work).ReloadAsync();
        Assert.Equal("AwaitingCitizen",item.Status);Assert.Equal("CitizenConfirmation",work.Stage);Assert.Equal(previousVersion,work.Version);Assert.Equal(previousItemVersion,item.Version);
        var summary=await community.SummaryAsync(item.Id);Assert.Equal(1,summary.Fixed);Assert.Equal(0,summary.NotFixed);
        var duplicate=await Feedback(neighbour,Fields(item.Reference,"NotFixed"));Assert.Equal(HttpStatusCode.OK,duplicate.StatusCode);Assert.Equal(1,(await community.SummaryAsync(item.Id)).Fixed);Assert.Equal(0,(await community.SummaryAsync(item.Id)).NotFixed);
        await workflow.RespondAsync(code,work.Version,true,"Water supply now works at this location");
        using var secondNeighbour=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(HttpStatusCode.Redirect,(await Feedback(secondNeighbour,Fields(item.Reference,"NotFixed"))).StatusCode);
        await db.Entry(item).ReloadAsync();await db.Entry(work).ReloadAsync();Assert.Equal("Closed",item.Status);Assert.Equal("Closed",work.Stage);
        summary=await community.SummaryAsync(item.Id);Assert.Equal(1,summary.Fixed);Assert.Equal(1,summary.NotFixed);Assert.Equal(2,(await community.ReportsAsync(item.Id)).Count);
        Assert.True(await db.CaseNotifications.AnyAsync(n=>n.CaseId==item.Id&&n.Recipient==ids.Officer.ToString()&&n.EventKey.StartsWith("community:")&&n.Severity=="Review"));
        Assert.False(await db.CaseNotifications.AnyAsync(n=>n.CaseId==item.Id&&n.EventKey.StartsWith("community:")&&(n.Recipient=="citizen"||n.Recipient==ids.Other.ToString())));
        Assert.All(await db.CommunityVerifications.Where(r=>r.CaseId==item.Id).ToListAsync(),r=>Assert.Matches("^[A-F0-9]{64}$",r.BrowserHash));
        var publicPage=await neighbour.GetStringAsync("/Community?reference="+item.Reference);var map=await neighbour.GetStringAsync("/PublicMap?handler=Data");
        foreach(var text in new[]{publicPage,map}){Assert.DoesNotContain("Private community observation",text);Assert.DoesNotContain("555012",text);Assert.DoesNotContain(code,text);}
        using var officer=app.CreateClient(new(){AllowAutoRedirect=false});await Post(officer,"/Account/Login",new(){["Email"]="water@example.test",["Password"]="Synthetic-password-123"});
        Assert.Contains("Private community observation",await officer.GetStringAsync("/Work/Case/"+item.Id));
        using var other=app.CreateClient(new(){AllowAutoRedirect=false});await Post(other,"/Account/Login",new(){["Email"]="other@example.test",["Password"]="Synthetic-password-123"});
        Assert.DoesNotContain("Private community observation",await (await other.GetAsync("/Work/Case/"+item.Id)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect,(await neighbour.GetAsync("/Work/Case/"+item.Id)).StatusCode);
        item.PublicMapConsent=false;await db.SaveChangesAsync();Assert.Equal(HttpStatusCode.NotFound,(await neighbour.GetAsync("/Community?reference="+item.Reference)).StatusCode);
    }

    [Fact]
    public async Task FormRequiresCsrfConsentAndProtectedBrowserCookie()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var item=await Eligible(scope.ServiceProvider);
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/Community",new FormUrlEncodedContent(Fields(item.Reference)))).StatusCode);
        var fields=Fields(item.Reference);fields["Consent"]="false";Assert.Equal(HttpStatusCode.OK,(await Feedback(client,fields)).StatusCode);
        Assert.Equal(0,(await scope.ServiceProvider.GetRequiredService<CommunityFeedbackService>().SummaryAsync(item.Id)).Fixed);
        using var stranger=app.CreateClient(new(){AllowAutoRedirect=false});
        // Obtain a valid antiforgery token elsewhere without establishing a community-browser identity.
        var unrelated=await stranger.GetStringAsync("/Submit");fields=Fields(item.Reference);fields["__RequestVerificationToken"]=Token(unrelated);
        Assert.Equal(HttpStatusCode.BadRequest,(await stranger.PostAsync("/Community",new FormUrlEncodedContent(fields))).StatusCode);
        stranger.DefaultRequestHeaders.Add("Cookie","Praja.CommunityBrowser=tampered");
        Assert.Equal(HttpStatusCode.BadRequest,(await stranger.PostAsync("/Community",new FormUrlEncodedContent(fields))).StatusCode);
        fields.Remove("Reference");Assert.Equal(HttpStatusCode.NotFound,(await stranger.PostAsync("/Community",new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await stranger.GetAsync("/Community")).StatusCode);
        using var fresh=app.CreateClient(new(){AllowAutoRedirect=false});var first=await fresh.GetAsync("/Community?reference="+item.Reference);
        var cookie=Assert.Single(first.Headers.GetValues("Set-Cookie"),v=>v.StartsWith("Praja.CommunityBrowser="));
        Assert.Contains("httponly",cookie.ToLowerInvariant());Assert.Contains("samesite=strict",cookie.ToLowerInvariant());
    }

    [Theory]
    [InlineData(13.3,76.6,12)]
    [InlineData(91,76.6,12)]
    [InlineData(12.3051,181,12)]
    [InlineData(12.3051,76.6551,201)]
    [InlineData(12.3051,76.6551,-1)]
    [InlineData(12.3051,76.6551,0)]
    [InlineData(double.NaN,76.6551,12)]
    public async Task InvalidOrDistantLocationsDoNotRecordFeedback(double latitude,double longitude,double accuracy)
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var item=await Eligible(scope.ServiceProvider);var service=scope.ServiceProvider.GetRequiredService<CommunityFeedbackService>();
        await Assert.ThrowsAsync<ArgumentException>(()=>service.SubmitAsync(item.Reference,Guid.NewGuid().ToString(),"Fixed","Observed repair",latitude,longitude,accuracy));
        Assert.Equal(0,(await service.SummaryAsync(item.Id)).Fixed);
    }

    [Fact]
    public async Task InvalidDecisionOversizedNotesAndMissingBrowserIdentityAreRejected()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var item=await Eligible(scope.ServiceProvider);var service=scope.ServiceProvider.GetRequiredService<CommunityFeedbackService>();
        foreach(var (browser,decision,note) in new[]{("", "Fixed", "Observed"),(Guid.NewGuid().ToString(),"Close", "Observed"),(Guid.NewGuid().ToString(),"Fixed",new string('x',501))})
            await Assert.ThrowsAsync<ArgumentException>(()=>service.SubmitAsync(item.Reference,browser,decision,note,12.3051,76.6551,12));
        Assert.Empty(await service.ReportsAsync(item.Id));
    }

    [Theory]
    [InlineData(false,false,"CitizenConfirmation")]
    [InlineData(true,true,"CitizenConfirmation")]
    [InlineData(true,false,"InProgress")]
    public async Task PrivateSampleAndUnfinishedWorkCannotCollectCommunityFeedback(bool consent,bool sample,string stage)
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var item=await Eligible(scope.ServiceProvider);var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        item.PublicMapConsent=consent;item.IsSample=sample;(await db.CaseWorkflows.SingleAsync()).Stage=stage;await db.SaveChangesAsync();
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/Community?reference="+item.Reference)).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<CommunityFeedbackService>().SubmitAsync(item.Reference,Guid.NewGuid().ToString(),"Fixed","Observed repair",12.3051,76.6551,12));
    }
}
