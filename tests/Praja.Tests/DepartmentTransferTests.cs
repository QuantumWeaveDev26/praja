using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;
namespace Praja.Tests;
public class DepartmentTransferTests
{
    [Fact]
    public async Task CrossDepartmentTransferContinuesThroughHttpEvidenceVerificationAndCitizenApproval()
    {
        using var app=new TestApp();Guid officerId, destination;
        using(var scope=app.Services.CreateScope()){
            var ids=await Seed(scope.ServiceProvider);officerId=ids.Officer;destination=ids.Other;
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
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,(await adminClient.PostAsync(path+"?handler=Transfer",new FormUrlEncodedContent(new Dictionary<string,string>{["Officer"]=destination.ToString()}))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,(await Post(department,path+"?handler=Transfer",new(){["Version"]="4",["Officer"]=destination.ToString(),["Note"]="Department cannot transfer itself"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(adminClient,path+"?handler=Transfer",new(){["Version"]="4",["Officer"]=destination.ToString(),["Note"]="Inspection found blocked sanitation drainage"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await department.GetAsync(path)).StatusCode);
        using var destinationClient=app.CreateClient(new(){AllowAutoRedirect=false});await Post(destinationClient,"/Account/Login",new(){["Email"]="other@example.test",["Password"]="Synthetic-password-123"});
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(destinationClient,path+"?handler=Act",new(){["Version"]="5",["Action"]="Accept",["Note"]="Sanitation team accepts transferred work"})).StatusCode);
        Assert.Contains("Inspection found blocked sanitation drainage",await citizen.GetStringAsync("/Track"));
        var uploadPage=await destinationClient.GetStringAsync(path);var token=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(uploadPage,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=");
        using var multipart=new MultipartFormDataContent();multipart.Add(new StringContent(token),"__RequestVerificationToken");multipart.Add(new StringContent("6"),"Version");multipart.Add(new StringContent("true"),"Checked");multipart.Add(new StringContent("Fictional completion image"),"Note");multipart.Add(new ByteArrayContent(bytes),"Upload","completion.png");
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await destinationClient.PostAsync(path+"?handler=Upload",multipart)).StatusCode);
        Guid photo;using(var scope=app.Services.CreateScope()){photo=(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().EvidenceAttachments.SingleAsync()).Id;}
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(destinationClient,path+"?handler=Act",new(){["Version"]="6",["Action"]="Complete",["Note"]="Fictional test: water supply restored and tested onsite.",["Photo"]=photo.ToString(),["Latitude"]="12.3",["Longitude"]="76.6",["Captured"]=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm"),["Checked"]="true"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await Post(citizen,"/Track?handler=Photo",new())).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(reviewer,path+"?handler=Act",new(){["Version"]="7",["Action"]="Verify",["Note"]="Fictional test: report, photo and location checked.",["Checked"]="true"})).StatusCode);
        var photoResponse=await Post(citizen,"/Track?handler=Photo",new());Assert.Equal(bytes,await photoResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("Repair crew reached the location",await citizen.GetStringAsync("/Track"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Respond",new(){["version"]="8",["decision"]="Confirm",["note"]="Fictional test: water supply works now."})).StatusCode);
        var closedPage=await citizen.GetStringAsync("/Track");Assert.Contains("Closed",closedPage);Assert.Contains("Citizen confirmed resolution",closedPage);
        using var final=app.Services.CreateScope();var closed=await final.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync(g=>g.Id==id);Assert.Equal("Closed",closed.Status);Assert.Equal("Confirmed",closed.CitizenFeedback);
        var notificationDb=final.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var reviewerId=(await notificationDb.Operators.SingleAsync(o=>o.Email=="review@example.test")).Id;
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient==reviewerId.ToString()&&n.EventKey=="workflow:7"));
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient=="citizen"&&n.Message.Contains("Citizen confirmed")));
        Assert.Contains("Case notifications",closedPage);
        var publicPage=await citizen.GetStringAsync("/Transparency");Assert.DoesNotContain("Ward 12",publicPage);Assert.DoesNotContain(code,publicPage);Assert.DoesNotContain("Fictional test",publicPage);
    }

    [Theory]
    [InlineData("Verification")]
    [InlineData("CitizenConfirmation")]
    [InlineData("Closed")]
    public async Task ProtectedStagesCannotTransfer(string stage)
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var ids=await Seed(scope.ServiceProvider);var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var service=scope.ServiceProvider.GetRequiredService<WorkflowService>();
        await service.AssignAsync(ids.Admin,ids.Case,ids.Officer,DateTime.UtcNow.AddDays(2),1);var work=await db.CaseWorkflows.SingleAsync();work.Stage=stage;await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.TransferAsync(ids.Admin,ids.Case,ids.Other,work.Version,"Needs another department"));Assert.Equal(stage,work.Stage);Assert.Equal(ids.Officer,work.AssignedOfficerId);
    }
    [Fact]
    public async Task ValidatesTargetVersionReasonAndPreservesDeadlineEvidenceAndAccess()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();
        await service.AssignAsync(ids.Admin,ids.Case,ids.Officer,DateTime.UtcNow.AddDays(2),1);var work=await db.CaseWorkflows.SingleAsync();var item=await db.Grievances.SingleAsync();var code=await service.IssueAsync(ids.Admin,item.Id);var due=work.DueUtc;item.CategoryCode="old-category";item.SubcategoryCode="old-issue";item.ResolutionReport="Earlier report retained";await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.TransferAsync(ids.Admin,item.Id,ids.Other,work.Version-1,"Wrong version rejected"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.TransferAsync(ids.Admin,item.Id,ids.Other,work.Version,""));
        foreach(var target in new[]{ids.Officer,ids.Reviewer,Guid.NewGuid()})await Assert.ThrowsAsync<InvalidOperationException>(()=>service.TransferAsync(ids.Admin,item.Id,target,work.Version,"Wrong target rejected"));
        var other=await db.Operators.SingleAsync(o=>o.Id==ids.Other);other.IsDisabled=true;await db.SaveChangesAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>service.TransferAsync(ids.Admin,item.Id,ids.Other,work.Version,"Disabled target rejected"));other.IsDisabled=false;await db.SaveChangesAsync();
        await service.CreateOfficerAsync(ids.Admin,"Intake officer","intake@example.test","Synthetic-password-123","Intake","");var intake=await db.Operators.SingleAsync(o=>o.Email=="intake@example.test");
        await service.TransferAsync(intake.Id,item.Id,ids.Other,work.Version,"Inspection confirms sanitation responsibility");
        Assert.Equal("sanitation",item.Department);Assert.Equal("Assigned",work.Stage);Assert.Equal(due,work.DueUtc);Assert.Equal("",item.CategoryCode);Assert.Equal("",item.SubcategoryCode);Assert.Equal("Earlier report retained",item.ResolutionReport);Assert.NotNull(await service.CitizenAsync(code));
        Assert.False(await service.CanReadAsync(ids.Officer,item));Assert.True(await service.CanReadAsync(ids.Other,item));Assert.Empty(await new NotificationService(db).ForOfficer(ids.Officer).ToListAsync());Assert.True(await db.CaseNotifications.AnyAsync(n=>n.CaseId==item.Id&&n.Recipient==ids.Other.ToString()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActAsync(ids.Other,item.Id,work.Version,"Progress","Must accept transfer first"));
    }
}
