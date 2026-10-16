using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;

namespace Praja.Tests;

public class ContractorWorkflowTests
{
    [Fact]
    public async Task CitizenSubmissionThroughContractorHttpFormsAndBothReviewsEndsInCitizenApproval()
    {
        using var app=new TestApp();Guid officerId,crewId;
        using(var scope=app.Services.CreateScope()){
            var ids=await Seed(scope.ServiceProvider);officerId=ids.Officer;crewId=await Contractor(scope.ServiceProvider,ids.Admin);
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var admin=await db.Operators.SingleAsync(o=>o.Id==ids.Admin);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");var crewAccount=await db.Operators.SingleAsync(o=>o.Id==crewId);crewAccount.MustChangePassword=false;await db.SaveChangesAsync();
        }
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});
        var key=Guid.NewGuid().ToString();var fields=new Dictionary<string,string>{["SubmissionKey"]=key,["Original"]="Fictional test: no water supply in Ward 12, Mysuru, for three days.",["District"]="Mysuru",["Location"]="Ward 12",["Language"]="English",["Consent"]="true",["Sample"]="true"};
        var submitted=await Post(citizen,"/Submit",fields);var receiptPage=await submitted.Content.ReadAsStringAsync();Assert.Contains("Complaint received",receiptPage);
        var code=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(receiptPage,"<textarea[^>]*id=\"receipt-code\"[^>]*>(.*?)</textarea>",System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value.Trim());Assert.NotEmpty(code);
        await Post(citizen,"/Submit",fields);
        Guid id;using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var receipt=await db.CitizenReceipts.SingleAsync();id=receipt.Id;Assert.Equal(2,await db.Grievances.CountAsync());}
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Open",new(){["Code"]=code})).StatusCode);
        Assert.Contains("awaiting officer assignment",await citizen.GetStringAsync("/Track"));
        using var adminClient=app.CreateClient(new(){AllowAutoRedirect=false});using var department=app.CreateClient(new(){AllowAutoRedirect=false});using var reviewer=app.CreateClient(new(){AllowAutoRedirect=false});using var crew=app.CreateClient(new(){AllowAutoRedirect=false});
        foreach(var (client,email) in new[]{(adminClient,"admin@example.test"),(department,"water@example.test"),(reviewer,"review@example.test"),(crew,"crew@example.test")})Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(client,"/Account/Login",new(){["Email"]=email,["Password"]="Synthetic-password-123"})).StatusCode);
        var path="/Work/Case/"+id;
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(adminClient,path+"?handler=Assign",new(){["Version"]="1",["Officer"]=officerId.ToString(),["Due"]=DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd")})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=Act",new(){["Version"]="2",["Action"]="Accept",["Note"]="Accepted the water complaint"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=AssignContractor",new(){["Version"]="3",["Officer"]=crewId.ToString(),["Note"]="Assign water repair field team"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,(await crew.GetAsync("/Work/Team")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await crew.GetAsync("/AiSettings")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(crew,path+"?handler=Act",new(){["Version"]="4",["Action"]="FieldAccept",["Note"]="Field team accepts repair assignment"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(crew,path+"?handler=Act",new(){["Version"]="5",["Action"]="FieldProgress",["Note"]="Repair crew reached the location"})).StatusCode);
        var uploadPage=await crew.GetStringAsync(path);var token=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(uploadPage,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=");
        using var multipart=new MultipartFormDataContent();multipart.Add(new StringContent(token),"__RequestVerificationToken");multipart.Add(new StringContent("6"),"Version");multipart.Add(new StringContent("true"),"Checked");multipart.Add(new StringContent("Fictional completion image"),"Note");multipart.Add(new ByteArrayContent(bytes),"Upload","completion.png");
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await crew.PostAsync(path+"?handler=Upload",multipart)).StatusCode);
        Guid photo;using(var scope=app.Services.CreateScope()){photo=(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().EvidenceAttachments.SingleAsync()).Id;}
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(crew,path+"?handler=Act",new(){["Version"]="6",["Action"]="FieldComplete",["Note"]="Fictional test: water supply restored and tested onsite.",["Photo"]=photo.ToString(),["Latitude"]="12.3",["Longitude"]="76.6",["Captured"]=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm"),["Checked"]="true"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await Post(citizen,"/Track?handler=Photo",new())).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=Act",new(){["Version"]="7",["Action"]="ApproveField",["Note"]="Officer inspected the contractor evidence",["Checked"]="true"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(reviewer,path+"?handler=Act",new(){["Version"]="8",["Action"]="Verify",["Note"]="Fictional test: report, photo and location checked.",["Checked"]="true"})).StatusCode);
        var photoResponse=await Post(citizen,"/Track?handler=Photo",new());Assert.Equal(bytes,await photoResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("Repair crew reached the location",await citizen.GetStringAsync("/Track"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Respond",new(){["version"]="9",["decision"]="Confirm",["note"]="Fictional test: water supply works now."})).StatusCode);
        var closedPage=await citizen.GetStringAsync("/Track");Assert.Contains("Closed",closedPage);Assert.Contains("Citizen confirmed resolution",closedPage);
        using var final=app.Services.CreateScope();var closed=await final.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync(g=>g.Id==id);Assert.Equal("Closed",closed.Status);Assert.Equal("Confirmed",closed.CitizenFeedback);
        var notificationDb=final.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var reviewerId=(await notificationDb.Operators.SingleAsync(o=>o.Email=="review@example.test")).Id;
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient==reviewerId.ToString()&&n.EventKey=="workflow:8"));
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient=="citizen"&&n.Message.Contains("Citizen confirmed")));
        Assert.Contains("Case notifications",closedPage);
        var publicPage=await citizen.GetStringAsync("/Transparency");Assert.DoesNotContain("Ward 12",publicPage);Assert.DoesNotContain(code,publicPage);Assert.DoesNotContain("Fictional test",publicPage);
    }

    private const string Password="Synthetic-password-123";
    private static async Task<Guid> Contractor(IServiceProvider services,Guid admin,string email="crew@example.test",string department="water")
    {
        var service=services.GetRequiredService<WorkflowService>();
        await service.CreateOfficerAsync(admin,"Field crew",email,Password,"Contractor",department);
        var db=services.GetRequiredService<WorkspaceDb>();
        return (await db.Operators.SingleAsync(o=>o.Email==email)).Id;
    }
    private static async Task<EvidenceItem> Photo(IServiceProvider services,Grievance item)
    {
        var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=");
        using var stream=new MemoryStream(bytes);
        return await services.GetRequiredService<EvidenceService>().UploadAsync(item.Id,item.OwnerId,"Field crew",item.Version,new FormFile(stream,0,bytes.Length,"Upload","completion.png"),"Fictional repair photo",true);
    }
    private static async Task Start(WorkflowService service,WorkspaceDb db,Guid admin,Guid officer,Guid id,Guid contractor)
    {
        await service.AssignAsync(admin,id,officer,DateTime.UtcNow.AddDays(3),(await db.Grievances.SingleAsync(g=>g.Id==id)).Version);
        var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==id);
        await service.ActAsync(officer,id,work.Version,"Accept","Department accepted the complaint");
        await service.AssignContractorAsync(officer,id,contractor,work.Version,"Water repair crew assigned onsite");
    }

    [Fact]
    public async Task FieldWorkRequiresOfficerAndReviewerChecksBeforeCitizenCanApproveAndDisputeReturnsForRepair()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;
        var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();
        var crew=await Contractor(sp,ids.Admin);var account=await db.Operators.SingleAsync(o=>o.Id==crew);Assert.True(account.MustChangePassword);
        await Start(service,db,ids.Admin,ids.Officer,ids.Case,crew);var work=await db.CaseWorkflows.SingleAsync();var item=await db.Grievances.SingleAsync();
        var code=await service.IssueAsync(ids.Admin,item.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(crew,item.Id,work.Version,"FieldProgress","Repair not accepted yet"));
        await service.ActAsync(crew,item.Id,work.Version,"FieldAccept","Crew accepted field assignment");
        await service.ActAsync(crew,item.Id,work.Version,"FieldProgress","Crew replaced the damaged water pipe");
        var photo=await Photo(sp,item);
        await service.ActAsync(crew,item.Id,work.Version,"FieldComplete","Water flow restored and tested onsite",photo.Id,12.3,76.6,DateTime.UtcNow,true);
        Assert.Equal("OfficerReview",work.Stage);Assert.Equal(crew,work.CompletedBy);Assert.Null(work.VerifiedBy);Assert.Equal(ids.Officer,work.AssignedOfficerId);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Cannot bypass department review",checkedEvidence:true));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RespondAsync(code,work.Version,true,"Cannot close before verification"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Review confirmation is required"));
        await service.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Department checked repair photo and location",checkedEvidence:true);
        Assert.Equal("Verification",work.Stage);Assert.Equal(crew,work.CompletedBy);
        await service.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Independent reviewer checked repair evidence",checkedEvidence:true);
        await service.RespondAsync(code,work.Version,false,"Water pressure still drops in evening");
        Assert.Equal("InProgress",work.Stage);Assert.Equal(crew,work.ContractorId);Assert.Null(work.VerifiedBy);
        await service.ActAsync(crew,item.Id,work.Version,"FieldProgress","Crew returned and corrected water pressure");
        await service.ActAsync(crew,item.Id,work.Version,"FieldComplete","Second repair checked with residents onsite",photo.Id,12.3,76.6,DateTime.UtcNow,true);
        await service.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Department checked the follow-up repair",checkedEvidence:true);
        await service.ActAsync(ids.Reviewer,item.Id,work.Version,"Verify","Reviewer checked the follow-up evidence",checkedEvidence:true);
        await service.RespondAsync(code,work.Version,true,"Water pressure remains normal now");
        Assert.Equal("Closed",work.Stage);Assert.Equal("Confirmed",item.CitizenFeedback);Assert.NotNull(item.ClosedUtc);
        Assert.True(await db.WorkflowUpdates.AnyAsync(u=>u.CaseId==item.Id&&u.Note.Contains("damaged water pipe")));
        Assert.True(await db.CaseNotifications.AnyAsync(n=>n.CaseId==item.Id&&n.Recipient==crew.ToString()));
        Assert.True(await db.CaseNotifications.AnyAsync(n=>n.CaseId==item.Id&&n.Recipient=="citizen"));
    }

    [Fact]
    public async Task AssignmentValidatesAuthorityDepartmentAccountStageAndVersion()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);
        var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();var crew=await Contractor(sp,ids.Admin);var other=await Contractor(sp,ids.Admin,"othercrew@example.test","sanitation");
        await service.AssignAsync(ids.Admin,ids.Case,ids.Officer,DateTime.UtcNow.AddDays(2),1);var work=await db.CaseWorkflows.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.AssignContractorAsync(ids.Officer,ids.Case,crew,work.Version,"Accept department assignment first"));
        await service.ActAsync(ids.Officer,ids.Case,work.Version,"Accept","Department starts responsibility");
        foreach(var actor in new[]{ids.Admin,ids.Other,ids.Reviewer,crew})await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.AssignContractorAsync(actor,ids.Case,crew,work.Version,"Only assigned department may delegate"));
        foreach(var target in new[]{other,ids.Officer,ids.Reviewer,Guid.NewGuid()})await Assert.ThrowsAsync<InvalidOperationException>(()=>service.AssignContractorAsync(ids.Officer,ids.Case,target,work.Version,"Invalid field worker assignment"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.AssignContractorAsync(ids.Officer,ids.Case,crew,work.Version-1,"Stale assignment must fail"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.AssignContractorAsync(ids.Officer,ids.Case,crew,work.Version,""));
        var account=await db.Operators.SingleAsync(o=>o.Id==crew);account.IsDisabled=true;await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.AssignContractorAsync(ids.Officer,ids.Case,crew,work.Version,"Disabled field worker cannot work"));
        Assert.Null(work.ContractorId);
    }

    [Fact]
    public async Task ContractorCannotBypassOfficerReviewOrAccessAnotherAssignmentAndReplacementRevokesAccess()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);
        var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();var crew=await Contractor(sp,ids.Admin);var replacement=await Contractor(sp,ids.Admin,"replacement@example.test");
        await Start(service,db,ids.Admin,ids.Officer,ids.Case,crew);var work=await db.CaseWorkflows.SingleAsync();var item=await db.Grievances.SingleAsync();
        Assert.True(await service.CanReadAsync(crew,item));Assert.False(await service.CanReadAsync(replacement,item));
        foreach(var action in new[]{"Accept","Progress","Complete","ApproveField","Verify","Return"})await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ActAsync(crew,item.Id,work.Version,action,"Contractor cannot perform officer actions",checkedEvidence:true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ActAsync(replacement,item.Id,work.Version,"FieldAccept","Another contractor cannot take work"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(ids.Officer,item.Id,work.Version,"Complete","Cannot bypass assigned contractor"));
        await service.ActAsync(crew,item.Id,work.Version,"FieldAccept","Original crew accepted assignment");
        await service.AssignContractorAsync(ids.Officer,item.Id,replacement,work.Version,"Replace crew due to availability");
        Assert.False(work.ContractorAccepted);Assert.False(await service.CanReadAsync(crew,item));Assert.True(await service.CanReadAsync(replacement,item));
        Assert.Empty(await new NotificationService(db).ForOfficer(crew).ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ActAsync(crew,item.Id,work.Version,"FieldProgress","Former contractor cannot update case"));
    }

    [Fact]
    public async Task ActiveContractorCannotBeDisabledOrMovedAndTransferRemovesFieldAccess()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);
        var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();var crew=await Contractor(sp,ids.Admin);
        await Start(service,db,ids.Admin,ids.Officer,ids.Case,crew);var work=await db.CaseWorkflows.SingleAsync();var item=await db.Grievances.SingleAsync();var account=await db.Operators.SingleAsync(o=>o.Id==crew);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ManageOfficerAsync(ids.Admin,crew,account.AccountVersion,"Disable",account.Name,account.Email,"Contractor","water","","Cannot abandon active field work"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ManageOfficerAsync(ids.Admin,crew,account.AccountVersion,"Edit",account.Name,account.Email,"Contractor","sanitation","","Cannot move assigned field worker"));
        await service.ActAsync(crew,item.Id,work.Version,"FieldAccept","Crew accepted field repair work");
        var deadline=work.DueUtc;await service.TransferAsync(ids.Admin,item.Id,ids.Other,work.Version,"Inspection found sanitation responsibility");
        Assert.Null(work.ContractorId);Assert.False(work.ContractorAccepted);Assert.Equal(deadline,work.DueUtc);Assert.Equal("Assigned",work.Stage);Assert.False(await service.CanReadAsync(crew,item));
        Assert.Empty(await new NotificationService(db).ForOfficer(crew).ToListAsync());
        await service.ManageOfficerAsync(ids.Admin,crew,account.AccountVersion,"Disable",account.Name,account.Email,"Contractor","water","","No remaining field assignments");Assert.True(account.IsDisabled);
    }

    [Fact]
    public async Task FieldCompletionRequiresValidEvidenceAndOfficerCannotApproveCorruptedPhoto()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);
        var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();var crew=await Contractor(sp,ids.Admin);
        await Start(service,db,ids.Admin,ids.Officer,ids.Case,crew);var work=await db.CaseWorkflows.SingleAsync();var item=await db.Grievances.SingleAsync();
        await service.ActAsync(crew,item.Id,work.Version,"FieldAccept","Crew accepted field work");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(crew,item.Id,work.Version,"FieldComplete","Evidence is still missing"));
        var photo=await Photo(sp,item);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(crew,item.Id,work.Version,"FieldComplete","Invalid location must be rejected",photo.Id,91,76.6,DateTime.UtcNow,true));
        await service.ActAsync(crew,item.Id,work.Version,"FieldComplete","Water supply restored onsite",photo.Id,12.3,76.6,DateTime.UtcNow,true);
        var attachment=await db.EvidenceAttachments.SingleAsync();attachment.ProtectedContent=[1,2,3];await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(ids.Officer,item.Id,work.Version,"ApproveField","Corrupted evidence must block approval",checkedEvidence:true));
        Assert.Equal("OfficerReview",work.Stage);Assert.Null(work.VerifiedBy);
        await service.ActAsync(ids.Officer,item.Id,work.Version,"ReturnField","Provide a readable replacement completion photo");
        Assert.Equal("InProgress",work.Stage);Assert.True(work.ContractorAccepted);
    }
}
