using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;
public class TeamWorkflowTests
{
    [Fact]
    public async Task CitizenSubmissionToApprovalWorksThroughHttpFormsIncludingPrivatePhotoDownload()
    {
        using var app=new TestApp();Guid officerId;
        using(var scope=app.Services.CreateScope()){
            var ids=await Seed(scope.ServiceProvider);officerId=ids.Officer;
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var admin=await db.Operators.SingleAsync(o=>o.Id==ids.Admin);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");await db.SaveChangesAsync();
        }
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});
        var key=Guid.NewGuid().ToString();var fields=new Dictionary<string,string>{["SubmissionKey"]=key,["Original"]="Fictional test: no water supply in Ward 12, Mysuru, for three days.",["District"]="Mysuru",["Location"]="Ward 12",["Language"]="English",["Consent"]="true",["Sample"]="true"};
        var submitted=await Post(citizen,"/Submit",fields);var receiptPage=await submitted.Content.ReadAsStringAsync();Assert.Contains("Complaint received",receiptPage);
        var code=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(receiptPage,"<textarea[^>]*id=\"receipt-code\"[^>]*>(.*?)</textarea>",System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value.Trim());Assert.NotEmpty(code);
        await Post(citizen,"/Submit",fields);
        Guid id;using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var receipt=await db.CitizenReceipts.SingleAsync();id=receipt.Id;Assert.Equal(2,await db.Grievances.CountAsync());}
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Open",new(){["Code"]=code})).StatusCode);
        Assert.Contains("awaiting officer assignment",await citizen.GetStringAsync("/Track"));
        using var adminClient=app.CreateClient(new(){AllowAutoRedirect=false});using var department=app.CreateClient(new(){AllowAutoRedirect=false});using var reviewer=app.CreateClient(new(){AllowAutoRedirect=false});
        foreach(var (client,email) in new[]{(adminClient,"admin@example.test"),(department,"water@example.test"),(reviewer,"review@example.test")})Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(client,"/Account/Login",new(){["Email"]=email,["Password"]="Synthetic-password-123"})).StatusCode);
        var path="/Work/Case/"+id;
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(adminClient,path+"?handler=Assign",new(){["Version"]="1",["Officer"]=officerId.ToString(),["Due"]=DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd")})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=Act",new(){["Version"]="2",["Action"]="Accept",["Note"]="Accepted the water complaint"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=Act",new(){["Version"]="3",["Action"]="Progress",["Note"]="Repair crew reached the location"})).StatusCode);
        var uploadPage=await department.GetStringAsync(path);var token=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(uploadPage,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=");
        using var multipart=new MultipartFormDataContent();multipart.Add(new StringContent(token),"__RequestVerificationToken");multipart.Add(new StringContent("4"),"Version");multipart.Add(new StringContent("true"),"Checked");multipart.Add(new StringContent("Fictional completion image"),"Note");multipart.Add(new ByteArrayContent(bytes),"Upload","completion.png");
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await department.PostAsync(path+"?handler=Upload",multipart)).StatusCode);
        Guid photo;using(var scope=app.Services.CreateScope()){photo=(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().EvidenceAttachments.SingleAsync()).Id;}
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=Act",new(){["Version"]="4",["Action"]="Complete",["Note"]="Fictional test: water supply restored and tested onsite.",["Photo"]=photo.ToString(),["Latitude"]="12.3",["Longitude"]="76.6",["Captured"]=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm"),["Checked"]="true"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await Post(citizen,"/Track?handler=Photo",new())).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(reviewer,path+"?handler=Act",new(){["Version"]="5",["Action"]="Verify",["Note"]="Fictional test: report, photo and location checked.",["Checked"]="true"})).StatusCode);
        var photoResponse=await Post(citizen,"/Track?handler=Photo",new());Assert.Equal(bytes,await photoResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("Repair crew reached the location",await citizen.GetStringAsync("/Track"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Respond",new(){["version"]="6",["decision"]="Confirm",["note"]="Fictional test: water supply works now."})).StatusCode);
        var closedPage=await citizen.GetStringAsync("/Track");Assert.Contains("Closed",closedPage);Assert.Contains("Citizen confirmed resolution",closedPage);
        using var final=app.Services.CreateScope();var closed=await final.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync(g=>g.Id==id);Assert.Equal("Closed",closed.Status);Assert.Equal("Confirmed",closed.CitizenFeedback);
        var notificationDb=final.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var reviewerId=(await notificationDb.Operators.SingleAsync(o=>o.Email=="review@example.test")).Id;
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient==reviewerId.ToString()&&n.EventKey=="workflow:5"));
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient=="citizen"&&n.Message.Contains("Citizen confirmed")));
        Assert.Contains("Case notifications",closedPage);
        var publicPage=await citizen.GetStringAsync("/Transparency");Assert.DoesNotContain("Ward 12",publicPage);Assert.DoesNotContain(code,publicPage);Assert.DoesNotContain("Fictional test",publicPage);
    }
    [Theory]
    [InlineData("InProgress")]
    [InlineData("ClosureReview")]
    public async Task ExistingOpenWorkCanAdoptTeamWorkflowWithoutLosingReports(string status)
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var ids=await Seed(scope.ServiceProvider);var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var item=await db.Grievances.SingleAsync();
        item.Status=status;item.ResolutionReport="Existing report must survive assignment";await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<WorkflowService>().AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(3),item.Version);
        Assert.Equal("InProgress",(await db.CaseWorkflows.SingleAsync()).Stage);Assert.Equal("InProgress",item.Status);Assert.Equal("Existing report must survive assignment",item.ResolutionReport);
    }
    [Fact]
    public async Task WebFormsEnforceRolesCsrfAndDisplayCitizenTimeline()
    {
        using var app=new TestApp();Guid adminId,officerId,caseId;
        using(var scope=app.Services.CreateScope())
        {
            var ids=await Seed(scope.ServiceProvider);adminId=ids.Admin;officerId=ids.Officer;caseId=ids.Case;
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var admin=await db.Operators.SingleAsync(o=>o.Id==adminId);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");await db.SaveChangesAsync();
        }
        using var adminClient=app.CreateClient(new(){AllowAutoRedirect=false});
        await Post(adminClient,"/Account/Login",new(){["Email"]="admin@example.test",["Password"]="Synthetic-password-123"});
        var assign=await Post(adminClient,"/Work/Case/"+caseId+"?handler=Assign",new(){["Version"]="1",["Officer"]=officerId.ToString(),["Due"]=DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd")});
        Assert.Equal(System.Net.HttpStatusCode.Redirect,assign.StatusCode);
        using var officer=app.CreateClient(new(){AllowAutoRedirect=false});await Post(officer,"/Account/Login",new(){["Email"]="water@example.test",["Password"]="Synthetic-password-123"});
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,(await officer.GetAsync("/Work/Team")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await officer.GetAsync("/AiSettings")).StatusCode);
        var accept=await Post(officer,"/Work/Case/"+caseId+"?handler=Act",new(){["Version"]="2",["Action"]="Accept",["Note"]="Work accepted by assigned officer"});Assert.Equal(System.Net.HttpStatusCode.Redirect,accept.StatusCode);
        string code;using(var scope=app.Services.CreateScope()){code=await scope.ServiceProvider.GetRequiredService<WorkflowService>().IssueAsync(adminId,caseId);}
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});Assert.Equal(System.Net.HttpStatusCode.BadRequest,(await citizen.PostAsync("/Track?handler=Open",new FormUrlEncodedContent(new Dictionary<string,string>{{"Code",code}}))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Open",new(){["Code"]=code})).StatusCode);
        var page=await citizen.GetStringAsync("/Track");Assert.Contains("Work accepted by assigned officer",page);Assert.DoesNotContain(code,page);Assert.DoesNotContain("Submit my decision",page);
    }
    internal static async Task<HttpResponseMessage> Post(HttpClient client,string path,Dictionary<string,string> fields)
    {
        var page=await client.GetStringAsync(path.Split('?')[0]);var token=System.Text.RegularExpressions.Regex.Match(page,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        fields["__RequestVerificationToken"]=System.Net.WebUtility.HtmlDecode(token);return await client.PostAsync(path,new FormUrlEncodedContent(fields));
    }
    internal static async Task<(Guid Admin,Guid Officer,Guid Reviewer,Guid Other,Guid Case)> Seed(IServiceProvider services)
    {
        var db=services.GetRequiredService<WorkspaceDb>();
        var admin=new Operator{Name="Admin",Email="admin@example.test",PasswordHash="unused"};db.Operators.Add(admin);await db.SaveChangesAsync();
        var service=services.GetRequiredService<WorkflowService>();
        await service.CreateOfficerAsync(admin.Id,"Water officer","water@example.test","Synthetic-password-123","Department","water");
        await service.CreateOfficerAsync(admin.Id,"Reviewer","review@example.test","Synthetic-password-123","Reviewer","");
        await service.CreateOfficerAsync(admin.Id,"Other officer","other@example.test","Synthetic-password-123","Department","sanitation");
        foreach(var account in await db.Operators.ToListAsync())account.MustChangePassword=false;
        await db.SaveChangesAsync();
        var item=new Grievance{OwnerId=admin.Id,Reference="TEAM-TEST",Title="Water complaint",OriginalText="No water supply in Ward 12, Mysuru.",Department="water",Status="Registered",Location="Ward 12",District="Mysuru"};db.Grievances.Add(item);await db.SaveChangesAsync();
        return(admin.Id,(await db.Operators.SingleAsync(o=>o.Email=="water@example.test")).Id,(await db.Operators.SingleAsync(o=>o.Email=="review@example.test")).Id,(await db.Operators.SingleAsync(o=>o.Email=="other@example.test")).Id,item.Id);
    }
    [Fact]
    public async Task DepartmentReviewerAndCitizenCompleteAndDisputeWithoutOfficerImpersonatingCitizen()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var services=scope.ServiceProvider;var ids=await Seed(services);var db=services.GetRequiredService<WorkspaceDb>();var service=services.GetRequiredService<WorkflowService>();
        var item=await db.Grievances.SingleAsync();await service.AssignAsync(ids.Admin,ids.Case,ids.Officer,DateTime.UtcNow.AddDays(7),item.Version);
        var work=await db.CaseWorkflows.SingleAsync();Assert.False(await service.CanReadAsync(ids.Other,item));Assert.True(await service.CanReadAsync(ids.Reviewer,item));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ActAsync(ids.Other,ids.Case,work.Version,"Accept","Starting work"));
        await service.ActAsync(ids.Officer,ids.Case,work.Version,"Accept","Officer accepted the work");
        await service.ActAsync(ids.Officer,ids.Case,work.Version,"Progress","Water supply repairs started today");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(ids.Officer,ids.Case,work.Version,"Complete","Water supply restored"));
        var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=");
        using var stream=new MemoryStream(png);var file=new FormFile(stream,0,png.Length,"Upload","work.png");
        var attachment=await services.GetRequiredService<EvidenceService>().UploadAsync(item.Id,item.OwnerId,"Water officer",item.Version,file,"Completion photo",true);
        await service.ActAsync(ids.Officer,ids.Case,work.Version,"Complete","Water supply restored after repair and checked onsite.",attachment.Id,12.3,76.6,DateTime.UtcNow,true);
        Assert.Equal("Verification",work.Stage);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ActAsync(ids.Officer,ids.Case,work.Version,"Verify","I verified my own work",checkedEvidence:true));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>services.GetRequiredService<CaseService>().CloseAsync(item,item.Version,true,"Admin"));
        var token=await service.IssueAsync(ids.Admin,item.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RespondAsync(token,work.Version,true,"Looks good"));
        await service.ActAsync(ids.Reviewer,ids.Case,work.Version,"Verify","Photo and location checked against complaint",checkedEvidence:true);
        Assert.Equal("AwaitingCitizen",item.Status);
        Assert.Equal(1,work.FeedbackRound);
        await service.RespondAsync(token,work.Version,false,"Water supply is still interrupted");Assert.Equal("InProgress",work.Stage);Assert.Equal("Disagreed",item.CitizenFeedback);
        Assert.Equal(2,work.FeedbackRound);
        await service.ActAsync(ids.Officer,ids.Case,work.Version,"Complete","Water supply repaired again and tested onsite.",attachment.Id,12.3,76.6,DateTime.UtcNow,true);
        await service.ActAsync(ids.Reviewer,ids.Case,work.Version,"Verify","Inspected the follow-up repair",checkedEvidence:true);
        await service.RespondAsync(token,work.Version,true,"Water supply now works");Assert.Equal("Closed",item.Status);Assert.NotNull(item.ClosedUtc);
        Assert.Equal(2,work.FeedbackRound);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RespondAsync(token,work.Version,true,"Repeated confirmation"));
        Assert.True(await db.WorkflowUpdates.CountAsync()>=8);
    }
    [Fact]
    public async Task CodesRotateAndWrongDepartmentAndStaleVersionsAreRejected()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var services=scope.ServiceProvider;var ids=await Seed(services);var db=services.GetRequiredService<WorkspaceDb>();var service=services.GetRequiredService<WorkflowService>();var item=await db.Grievances.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.AssignAsync(ids.Admin,item.Id,ids.Other,DateTime.UtcNow.AddDays(3),item.Version));
        await service.AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(3),item.Version);
        var first=await service.IssueAsync(ids.Admin,item.Id);Assert.NotNull(await service.CitizenAsync(first));var next=await service.IssueAsync(ids.Admin,item.Id);Assert.Null(await service.CitizenAsync(first));Assert.Null(await service.CitizenAsync(next+"tampered"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.IssueAsync(ids.Officer,item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(ids.Officer,item.Id,1,"Accept","Start work today"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.CreateOfficerAsync(ids.Officer,"Bad admin","bad@example.test","Synthetic-password-123","Reviewer",""));
        Assert.Equal("Assigned",(await db.CaseWorkflows.SingleAsync()).Stage);
    }
}
