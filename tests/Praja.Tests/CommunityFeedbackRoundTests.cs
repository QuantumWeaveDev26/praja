using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;

namespace Praja.Tests;

public class CommunityFeedbackRoundTests
{
    private static string Hidden(string html,string name)=>WebUtility.HtmlDecode(Regex.Match(html,"name=\""+name+"\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task<Dictionary<string,string>> Form(HttpClient client,string reference,string decision)
    {
        var path="/Community?reference="+Uri.EscapeDataString(reference);var html=await client.GetStringAsync(path);
        if(Hidden(html,"FeedbackRound")=="")html=await client.GetStringAsync(path);
        var fields=new Dictionary<string,string>{["Reference"]=reference,["Decision"]=decision,["Note"]="Private observation for this repair",["Consent"]="true",["Latitude"]="12.3",["Longitude"]="76.6",["Accuracy"]="10",["FeedbackRound"]=Hidden(html,"FeedbackRound"),["__RequestVerificationToken"]=Hidden(html,"__RequestVerificationToken")};
        Assert.NotEmpty(fields["FeedbackRound"]);Assert.NotEmpty(fields["__RequestVerificationToken"]);return fields;
    }
    private static Task<HttpResponseMessage> Send(HttpClient client,Dictionary<string,string> form)=>client.PostAsync("/Community",new FormUrlEncodedContent(form));
    private static async Task<Guid> Photo(IServiceProvider sp,Grievance item,bool followUp)
    {
        var bytes=Convert.FromBase64String(followUp?"iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAD0lEQVR4nGNg+M/AwPAfAAYBAf9d/01yAAAAAElFTkSuQmCC":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=");using var stream=new MemoryStream(bytes);
        return (await sp.GetRequiredService<EvidenceService>().UploadAsync(item.Id,item.OwnerId,"Field crew",item.Version,new FormFile(stream,0,bytes.Length,"Upload","completion.png"),"Completed water repair",true)).Id;
    }

    [Fact]
    public async Task SameBrowserCanReviewNewRepairRoundButOldFormsReportsAndDuplicatesCannotAffectIt()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var flow=sp.GetRequiredService<WorkflowService>();var feedback=sp.GetRequiredService<CommunityFeedbackService>();var item=await db.Grievances.SingleAsync();
        item.PublicMapConsent=true;item.IncidentLatitude=12.3;item.IncidentLongitude=76.6;await db.SaveChangesAsync();
        await flow.AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(2),item.Version);var work=await db.CaseWorkflows.SingleAsync();var code=await flow.IssueAsync(ids.Admin,item.Id);
        await flow.ActAsync(ids.Officer,item.Id,work.Version,"Accept","Officer accepted water repair");
        await flow.CreateOfficerAsync(ids.Admin,"Crew","round-crew@example.test","Synthetic-password-123","Contractor","water");var crew=(await db.Operators.SingleAsync(o=>o.Email=="round-crew@example.test")).Id;
        await flow.AssignContractorAsync(ids.Officer,item.Id,crew,work.Version,"Repair interrupted water supply");await flow.ActAsync(crew,item.Id,work.Version,"FieldAccept","Crew accepted water repair");
        var photo=await Photo(sp,item,false);await flow.ActAsync(crew,item.Id,work.Version,"FieldComplete","Water repair completed onsite",photo,12.3,76.6,DateTime.UtcNow,true);
        await flow.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Officer checked water repair evidence",checkedEvidence:true);await flow.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Reviewer checked repaired water service",checkedEvidence:true);
        Assert.Equal(1,work.FeedbackRound);await flow.RespondAsync(code,work.Version,true,"Water supply now works");Assert.Equal(1,work.FeedbackRound);
        using var neighbour=app.CreateClient(new(){AllowAutoRedirect=false});var firstForm=await Form(neighbour,item.Reference,"NotFixed");Assert.Equal("1",firstForm["FeedbackRound"]);
        Assert.Equal(HttpStatusCode.Redirect,(await Send(neighbour,firstForm)).StatusCode);
        var report=await db.CommunityVerifications.SingleAsync();Assert.Equal(1,report.FeedbackRound);Assert.Equal(1,(await feedback.SummaryAsync(item.Id)).NotFixed);
        // A second pending report from the same old round must not reopen a later repair.
        var otherId=await feedback.SubmitAsync(item.Reference,Guid.NewGuid().ToString(),"NotFixed","Another old repair report",12.3,76.6,10);var otherReport=await db.CommunityVerifications.SingleAsync(r=>r.Id==otherId);
        await feedback.ReviewAsync(ids.Officer,item.Id,report.Id,report.Version,work.Version,"Investigate","Inspection found the repair failed");
        await feedback.ReviewAsync(ids.Officer,item.Id,report.Id,report.Version,work.Version,"Reopen","Further field repair is needed",DateTime.UtcNow.AddDays(3));
        Assert.Equal(2,work.FeedbackRound);Assert.Equal("InProgress",work.Stage);Assert.Equal(0,(await feedback.SummaryAsync(item.Id)).NotFixed);
        Assert.Equal(HttpStatusCode.NotFound,(await Send(neighbour,firstForm)).StatusCode);
        await flow.ActAsync(crew,item.Id,work.Version,"FieldAccept","Crew accepted follow-up repair");var secondPhoto=await Photo(sp,item,true);
        await flow.ActAsync(crew,item.Id,work.Version,"FieldComplete","Follow-up water repair completed",secondPhoto,12.3,76.6,DateTime.UtcNow,true);await flow.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Officer checked follow-up repair",checkedEvidence:true);await flow.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Reviewer checked follow-up repair",checkedEvidence:true);
        Assert.Equal("CitizenConfirmation",work.Stage);Assert.Equal(2,work.FeedbackRound);
        // The old token still has valid CSRF/session credentials but its round is stale.
        Assert.Equal(HttpStatusCode.OK,(await Send(neighbour,firstForm)).StatusCode);Assert.Equal(0,(await feedback.SummaryAsync(item.Id)).Fixed);Assert.Equal(0,(await feedback.SummaryAsync(item.Id)).NotFixed);
        var secondForm=await Form(neighbour,item.Reference,"Fixed");Assert.Equal("2",secondForm["FeedbackRound"]);Assert.Equal(HttpStatusCode.Redirect,(await Send(neighbour,secondForm)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(neighbour,secondForm)).StatusCode);
        var summary=await feedback.SummaryAsync(item.Id);Assert.Equal(1,summary.Fixed);Assert.Equal(0,summary.NotFixed);
        var rows=await db.CommunityVerifications.OrderBy(r=>r.CreatedUtc).ToListAsync();Assert.Equal(3,rows.Count);Assert.Equal(rows[0].BrowserHash,rows[2].BrowserHash);Assert.Equal(1,rows[0].FeedbackRound);Assert.Equal(2,rows[2].FeedbackRound);Assert.Equal(3,(await feedback.ReportsAsync(item.Id)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>feedback.ReviewAsync(ids.Officer,item.Id,otherReport.Id,otherReport.Version,work.Version,"Investigate","Old report must not reopen new work"));
        await flow.RespondAsync(code,work.Version,true,"Follow-up repair now works");Assert.Equal("Closed",work.Stage);Assert.Equal(2,work.FeedbackRound);Assert.Equal(1,(await feedback.SummaryAsync(item.Id)).Fixed);
        Assert.Equal(3,await db.CommunityVerifications.CountAsync());
        var history = await sp.GetRequiredService<PublicMapService>().HistoryAsync(item.Reference);
        Assert.NotNull(history);
        Assert.Equal(new[]{"Assigned","InProgress","OfficerReview","Verification","CitizenConfirmation","Closed","InProgress","OfficerReview","Verification","CitizenConfirmation","Closed"}.Select(Praja.Web.Domain.CaseRules.WorkflowLabel), history.Events.Select(e=>e.StageLabel));
        var publicHtml = await neighbour.GetStringAsync("/PublicHistory?reference="+item.Reference);
        Assert.Contains("Check completed work",publicHtml);
        foreach(var secret in new[]{"Private observation", "Inspection found", "Further field", "round-crew@example.test", code})Assert.DoesNotContain(secret,publicHtml);
    }

    [Fact]
    public async Task MissingOrInvalidRoundCannotSubmitAndRoundDefaultsPreserveExistingRecords()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();item.PublicMapConsent=true;item.IncidentLatitude=12.3;item.IncidentLongitude=76.6;item.Status="Closed";
        db.CaseWorkflows.Add(new(){Id=item.Id,AssignedOfficerId=ids.Officer,Stage="Closed",DueUtc=DateTime.UtcNow.AddDays(2)});await db.SaveChangesAsync();
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});var form=await Form(client,item.Reference,"Fixed");form.Remove("FeedbackRound");Assert.Equal(HttpStatusCode.OK,(await Send(client,form)).StatusCode);
        foreach(var invalid in new[]{"0","-1","999","bad"}){form["FeedbackRound"]=invalid;Assert.Equal(HttpStatusCode.OK,(await Send(client,form)).StatusCode);}
        Assert.Empty(await db.CommunityVerifications.ToListAsync());Assert.Equal(1,(await db.CaseWorkflows.SingleAsync()).FeedbackRound);
    }

    [Fact]
    public async Task RoundMigrationPreservesPriorReportsAndMakesThemRoundOne()
    {
        var path=Path.Combine(Path.GetTempPath(),"praja-community-round-upgrade-"+Guid.NewGuid().ToString("N")+".db");
        try
        {
            await using var db=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.MigrateAsync();var owner=new Operator{Email="round-upgrade@example.test",PasswordHash="preserved-hash"};db.Operators.Add(owner);await db.SaveChangesAsync();
            var item=new Grievance{OwnerId=owner.Id,Reference="ROUND-UPGRADE",Status="Closed",Version=7};db.Grievances.Add(item);await db.SaveChangesAsync();
            db.CaseWorkflows.Add(new(){Id=item.Id,AssignedOfficerId=owner.Id,Stage="Closed",Version=8,DueUtc=DateTime.UtcNow.AddDays(3)});
            db.CommunityVerifications.Add(new(){CaseId=item.Id,BrowserHash=new string('A',64),Decision="NotFixed",Note="Retain original community report",ReviewState="Investigating",ReviewNote="Retain officer investigation",ReviewedBy=owner.Id,Version=2});await db.SaveChangesAsync();
            var reopened=new Grievance{OwnerId=owner.Id,Reference="ROUND-REOPENED",Status="Closed"};db.Grievances.Add(reopened);await db.SaveChangesAsync();var cutoff=DateTime.UtcNow.AddDays(-1);
            db.CaseWorkflows.Add(new(){Id=reopened.Id,AssignedOfficerId=owner.Id,Stage="Closed",DueUtc=DateTime.UtcNow,ReopenedUtc=cutoff});
            db.CommunityVerifications.Add(new(){CaseId=reopened.Id,BrowserHash=new string('B',64),Decision="NotFixed",Note="Before repair",CreatedUtc=cutoff.AddHours(-1)});
            db.CommunityVerifications.Add(new(){CaseId=reopened.Id,BrowserHash=new string('C',64),Decision="Fixed",Note="After repair",CreatedUtc=cutoff.AddHours(1)});await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var previous=db.Database.GetMigrations().Single(m=>m.EndsWith("_CommunityReportReview"));await db.GetService<IMigrator>().MigrateAsync(previous);await db.Database.MigrateAsync();
            var saved=await db.CommunityVerifications.SingleAsync(r=>r.CaseId==item.Id);Assert.Equal(1,saved.FeedbackRound);Assert.Equal("Retain original community report",saved.Note);Assert.Equal("Retain officer investigation",saved.ReviewNote);Assert.Equal("Investigating",saved.ReviewState);Assert.Equal(2,saved.Version);
            var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==item.Id);Assert.Equal(1,work.FeedbackRound);Assert.Equal(8,work.Version);Assert.Equal("Closed",work.Stage);Assert.Equal(7,(await db.Grievances.SingleAsync(g=>g.Id==item.Id)).Version);Assert.Equal("preserved-hash",(await db.Operators.SingleAsync()).PasswordHash);
            Assert.Equal(2,(await db.CaseWorkflows.SingleAsync(w=>w.Id==reopened.Id)).FeedbackRound);Assert.Equal(1,(await db.CommunityVerifications.SingleAsync(r=>r.Note=="Before repair")).FeedbackRound);Assert.Equal(2,(await db.CommunityVerifications.SingleAsync(r=>r.Note=="After repair")).FeedbackRound);
            await db.Database.MigrateAsync();Assert.Equal(3,await db.CommunityVerifications.CountAsync());
        }
        finally {if(File.Exists(path)&&Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))File.Delete(path);}
    }
}
