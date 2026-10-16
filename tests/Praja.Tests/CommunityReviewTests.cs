using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;

namespace Praja.Tests;

public class CommunityReviewTests
{
    private const string Png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=";
    private static async Task<Guid> Photo(IServiceProvider sp,Grievance item,bool followUp=false)
    {
        var bytes=Convert.FromBase64String(followUp?"iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAD0lEQVR4nGNg+M/AwPAfAAYBAf9d/01yAAAAAElFTkSuQmCC":Png);using var stream=new MemoryStream(bytes);
        return (await sp.GetRequiredService<EvidenceService>().UploadAsync(item.Id,item.OwnerId,"Field crew",item.Version,new FormFile(stream,0,bytes.Length,"Upload","completion.png"),"Repair completion photo",true)).Id;
    }
    private static async Task<(Guid Admin,Guid Officer,Guid Reviewer,Guid Other,Guid Case)> Closed(IServiceProvider sp)
    {
        var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();
        item.PublicMapConsent=true;item.IncidentLatitude=12.3;item.IncidentLongitude=76.6;item.Status="Closed";item.ClosedUtc=DateTime.UtcNow;item.CitizenFeedback="Confirmed";
        db.CaseWorkflows.Add(new(){Id=item.Id,AssignedOfficerId=ids.Officer,Stage="Closed",DueUtc=DateTime.UtcNow.AddDays(2)});await db.SaveChangesAsync();return ids;
    }
    private static Task<Guid> Report(CommunityFeedbackService service,string reference,string decision="NotFixed")=>service.SubmitAsync(reference,Guid.NewGuid().ToString(),decision,"Private neighbour report 555012",12.3,76.6,10);

    [Fact]
    public async Task ClosedRepairCanBeInvestigatedReopenedAndRepairedAgainWithNewEvidenceAndSameCitizenCode()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();
        var flow=sp.GetRequiredService<WorkflowService>();var community=sp.GetRequiredService<CommunityFeedbackService>();var item=await db.Grievances.SingleAsync();
        item.PublicMapConsent=true;item.IncidentLatitude=12.3;item.IncidentLongitude=76.6;await db.SaveChangesAsync();
        await flow.AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(2),item.Version);var work=await db.CaseWorkflows.SingleAsync();
        var code=await flow.IssueAsync(ids.Admin,item.Id);
        await flow.ActAsync(ids.Officer,item.Id,work.Version,"Accept","Officer accepted repair assignment");
        await flow.CreateOfficerAsync(ids.Admin,"Field crew","review-crew@example.test","Synthetic-password-123","Contractor","water");var crew=(await db.Operators.SingleAsync(o=>o.Email=="review-crew@example.test")).Id;
        await flow.AssignContractorAsync(ids.Officer,item.Id,crew,work.Version,"Complete water supply repair");
        await flow.ActAsync(crew,item.Id,work.Version,"FieldAccept","Crew accepted field work");
        var oldPhoto=await Photo(sp,item);
        await flow.ActAsync(crew,item.Id,work.Version,"FieldComplete","Water supply restored onsite",oldPhoto,12.3,76.6,DateTime.UtcNow,true);
        await flow.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Officer checked the first repair",checkedEvidence:true);
        await flow.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Reviewer verified the first repair",checkedEvidence:true);
        await flow.RespondAsync(code,work.Version,true,"Water works after the first repair");Assert.Equal("Closed",work.Stage);
        var reportId=await Report(community,item.Reference);var report=await db.CommunityVerifications.SingleAsync(r=>r.Id==reportId);
        Assert.Equal("Closed",item.Status);
        await community.ReviewAsync(ids.Officer,item.Id,reportId,report.Version,work.Version,"Investigate","Private staff investigation 555019");
        Assert.Equal("Closed",work.Stage);Assert.Equal("Investigating",report.ReviewState);
        using var officer=app.CreateClient(new(){AllowAutoRedirect=false});await Post(officer,"/Account/Login",new(){["Email"]="water@example.test",["Password"]="Synthetic-password-123"});
        var path="/Work/Case/"+item.Id;
        Assert.Equal(HttpStatusCode.BadRequest,(await officer.PostAsync(path+"?handler=CommunityReview",new FormUrlEncodedContent(new Dictionary<string,string>{["ReportId"]=reportId.ToString(),["ReviewAction"]="Reopen"}))).StatusCode);
        var reopened=await Post(officer,path+"?handler=CommunityReview",new(){["ReportId"]=reportId.ToString(),["ReportVersion"]=report.Version.ToString(),["Version"]=work.Version.ToString(),["ReviewAction"]="Reopen",["ReviewNote"]="Private staff confirmed failed repair 555020",["ReviewDue"]=DateTime.UtcNow.AddDays(4).ToString("yyyy-MM-dd")});
        Assert.Equal(HttpStatusCode.Redirect,reopened.StatusCode);
        await db.Entry(work).ReloadAsync();await db.Entry(item).ReloadAsync();await db.Entry(report).ReloadAsync();
        Assert.Equal("Reopened",report.ReviewState);Assert.Equal("InProgress",work.Stage);Assert.Equal("InProgress",item.Status);Assert.Equal("Pending",item.CitizenFeedback);Assert.Null(item.ClosedUtc);
        Assert.Null(work.CompletionPhotoId);Assert.Null(work.Latitude);Assert.Null(work.Longitude);Assert.Null(work.CapturedUtc);Assert.Null(work.CompletedBy);Assert.Null(work.VerifiedBy);Assert.Equal("",work.CompletionNote);Assert.False(work.ContractorAccepted);Assert.NotNull(work.ReopenedUtc);
        Assert.NotNull(await flow.CitizenAsync(code));
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});await Post(citizen,"/Track?handler=Open",new(){["Code"]=code});
        Assert.Contains("reopen",(await citizen.GetStringAsync("/Track")).ToLowerInvariant());
        using(var snapshot=System.Text.Json.JsonDocument.Parse(await citizen.GetStringAsync("/PublicMap?handler=Data")))Assert.Equal("InProgress",Assert.Single(snapshot.RootElement.GetProperty("items").EnumerateArray()).GetProperty("stage").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>flow.RespondAsync(code,work.Version,true,"Cannot bypass repair verification"));
        await flow.ActAsync(crew,item.Id,work.Version,"FieldAccept","Crew accepted reopened field work");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>flow.ActAsync(crew,item.Id,work.Version,"FieldComplete","Reusing the original repair photo",oldPhoto,12.3,76.6,DateTime.UtcNow,true));
        var newPhoto=await Photo(sp,item,true);Assert.True(await db.EvidenceAttachments.AnyAsync(e=>e.Id==oldPhoto));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>flow.ActAsync(crew,item.Id,work.Version,"FieldComplete","New photo with stale capture timestamp",newPhoto,12.3,76.6,work.ReopenedUtc!.Value.AddMinutes(-1),true));
        await flow.ActAsync(crew,item.Id,work.Version,"FieldComplete","Follow-up water repair completed onsite",newPhoto,12.3,76.6,DateTime.UtcNow,true);
        await flow.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Officer checked the follow-up repair",checkedEvidence:true);
        await flow.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Reviewer verified the follow-up repair",checkedEvidence:true);
        await flow.RespondAsync(code,work.Version,true,"Water works after the follow-up repair");Assert.Equal("Closed",item.Status);Assert.Equal(newPhoto,work.CompletionPhotoId);
        using(var snapshot=System.Text.Json.JsonDocument.Parse(await citizen.GetStringAsync("/PublicMap?handler=Data")))Assert.Equal("Closed",Assert.Single(snapshot.RootElement.GetProperty("items").EnumerateArray()).GetProperty("stage").GetString());
        foreach(var url in new[]{"/Track","/Community?reference="+item.Reference,"/PublicMap?handler=Data"})
        {
            var text=await citizen.GetStringAsync(url);Assert.DoesNotContain("555019",text);Assert.DoesNotContain("555020",text);Assert.DoesNotContain("555012",text);
        }
        Assert.Contains("555020",await officer.GetStringAsync(path));
        Assert.Contains(await db.CaseNotifications.Where(n=>n.CaseId==item.Id&&n.Recipient=="citizen").ToListAsync(),n=>n.Message.Contains("reopen",StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAssignedDepartmentOrAdminCanReviewNotFixedReports(bool awaitingCitizen)
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Closed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<CommunityFeedbackService>();var flow=sp.GetRequiredService<WorkflowService>();
        var item=await db.Grievances.SingleAsync();var work=await db.CaseWorkflows.SingleAsync();var id=await Report(service,item.Reference);var report=await db.CommunityVerifications.SingleAsync(r=>r.Id==id);
        if(awaitingCitizen){item.Status="AwaitingCitizen";item.ClosedUtc=null;item.CitizenFeedback="Pending";work.Stage="CitizenConfirmation";await db.SaveChangesAsync();}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Admin,item.Id,id,report.Version,work.Version,"Reopen","Must investigate before reopening",DateTime.UtcNow.AddDays(2)));
        await flow.CreateOfficerAsync(ids.Admin,"Intake","community-intake@example.test","Synthetic-password-123","Intake","");await flow.CreateOfficerAsync(ids.Admin,"Crew","community-othercrew@example.test","Synthetic-password-123","Contractor","water");
        var extra=await db.Operators.Where(o=>o.Email=="community-intake@example.test"||o.Email=="community-othercrew@example.test").Select(o=>o.Id).ToListAsync();
        foreach(var actor in extra.Concat(new[]{ids.Other,ids.Reviewer}))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReviewAsync(actor,item.Id,id,report.Version,work.Version,"Investigate","Private staff investigation"));
        await service.ReviewAsync(ids.Admin,item.Id,id,report.Version,work.Version,"Investigate","Admin investigates the field report");Assert.Equal("Investigating",report.ReviewState);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Admin,item.Id,id,report.Version,work.Version,"Reopen","",DateTime.UtcNow.AddDays(3)));
        foreach(var due in new DateTime?[]{null,DateTime.UtcNow.AddDays(-1),DateTime.UtcNow.AddYears(2)})
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Admin,item.Id,id,report.Version,work.Version,"Reopen","Admin confirmed further repair is needed",due));
        await service.ReviewAsync(ids.Admin,item.Id,id,report.Version,work.Version,"Reopen","Admin confirmed work requires follow-up",DateTime.UtcNow.AddDays(3));Assert.Equal("InProgress",work.Stage);
    }

    [Fact]
    public async Task DismissRequiresReasonAndRejectsStaleRepeatedWrongCaseAndFixedReportActions()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Closed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<CommunityFeedbackService>();var item=await db.Grievances.SingleAsync();var work=await db.CaseWorkflows.SingleAsync();
        var id=await Report(service,item.Reference);var report=await db.CommunityVerifications.SingleAsync(r=>r.Id==id);var originalWorkVersion=work.Version;var originalReportVersion=report.Version;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,item.Id,id,report.Version,work.Version,"Dismiss",""));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,item.Id,id,report.Version-1,work.Version,"Investigate","Investigate the field observation"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,item.Id,id,report.Version,work.Version-1,"Investigate","Investigate the field observation"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,Guid.NewGuid(),id,report.Version,work.Version,"Investigate","Wrong case must not be accepted"));
        var otherCase=new Grievance{OwnerId=ids.Admin,Reference="OTHER-COMMUNITY-CASE",Department="water",Status="Closed"};db.Grievances.Add(otherCase);db.CaseWorkflows.Add(new(){Id=otherCase.Id,AssignedOfficerId=ids.Officer,Stage="Closed",DueUtc=DateTime.UtcNow.AddDays(2)});await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,otherCase.Id,id,report.Version,1,"Investigate","Report belongs to another complaint"));
        await service.ReviewAsync(ids.Officer,item.Id,id,report.Version,work.Version,"Dismiss","Inspection confirms this report was mistaken");Assert.Equal("Dismissed",report.ReviewState);Assert.Equal("Closed",work.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,item.Id,id,originalReportVersion,originalWorkVersion,"Dismiss","Repeated original dismissal action"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,item.Id,id,report.Version,work.Version,"Reopen","Cannot reopen dismissed report",DateTime.UtcNow.AddDays(2)));
        var fixedId=await Report(service,item.Reference,"Fixed");var fixedReport=await db.CommunityVerifications.SingleAsync(r=>r.Id==fixedId);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReviewAsync(ids.Officer,item.Id,fixedId,fixedReport.Version,work.Version,"Investigate","Cannot investigate a fixed confirmation"));
    }
}
