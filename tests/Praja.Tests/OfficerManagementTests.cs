using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
namespace Praja.Tests;
public class OfficerManagementTests
{
    [Theory]
    [InlineData("Verification")]
    [InlineData("CitizenConfirmation")]
    public async Task ReassignmentCannotBypassReviewOrAllowOriginalWorkerToVerify(string stage)
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await TeamWorkflowTests.Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();
        await service.CreateOfficerAsync(ids.Admin,"Replacement","replacement@example.test","Synthetic-password-123","Department","water");var replacement=await db.Operators.SingleAsync(o=>o.Email=="replacement@example.test");
        await service.AssignAsync(ids.Admin,ids.Case,ids.Officer,DateTime.UtcNow.AddDays(3),1);var work=await db.CaseWorkflows.SingleAsync();work.Stage=stage;work.CompletionPhotoId=Guid.NewGuid();work.CompletionNote="Existing evidence report";await db.SaveChangesAsync();
        await service.ReassignAsync(ids.Admin,ids.Case,replacement.Id,work.Version,"Original officer transferred");Assert.Equal(stage,work.Stage);Assert.Equal(ids.Officer,work.CompletedBy);Assert.Equal("Existing evidence report",work.CompletionNote);
        await service.ManageOfficerAsync(ids.Admin,ids.Officer,0,"Edit","Former worker","water@example.test","Reviewer","","","Changed responsibilities");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ActAsync(ids.Officer,ids.Case,work.Version,"Verify","Attempt to verify own earlier work",checkedEvidence:true));
        work.Stage="Closed";await db.SaveChangesAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReassignAsync(ids.Admin,ids.Case,replacement.Id,work.Version,"Cannot move closed case"));
    }
    [Fact]
    public async Task AdminManagementFormRequiresCsrfAndSavesProfile()
    {
        using var app=new TestApp();Guid target;
        using(var scope=app.Services.CreateScope()){var ids=await TeamWorkflowTests.Seed(scope.ServiceProvider);target=ids.Officer;var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var admin=await db.Operators.SingleAsync(o=>o.Id==ids.Admin);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");await db.SaveChangesAsync();}
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});await TeamWorkflowTests.Post(client,"/Account/Login",new(){["Email"]="admin@example.test",["Password"]="Synthetic-password-123"});
        var fields=new Dictionary<string,string>{["Target"]=target.ToString(),["Version"]="0",["Action"]="Edit",["Name"]="Updated officer",["Email"]="water@example.test",["Role"]="Department",["Department"]="water",["Reason"]="Corrected display name"};
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,(await client.PostAsync("/Work/Team?handler=Manage",new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await TeamWorkflowTests.Post(client,"/Work/Team?handler=Manage",fields)).StatusCode);
        using var check=app.Services.CreateScope();Assert.Equal("Updated officer",(await check.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync(o=>o.Id==target)).Name);
    }
    [Fact]
    public async Task ReassignmentPreservesHistoryDeadlineAndCitizenAccessAndRevokesOldOfficer()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await TeamWorkflowTests.Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();
        await service.CreateOfficerAsync(ids.Admin,"Replacement","replacement@example.test","Synthetic-password-123","Department","water");var replacement=await db.Operators.SingleAsync(o=>o.Email=="replacement@example.test");
        var item=await db.Grievances.SingleAsync();await service.AssignAsync(ids.Admin,item.Id,ids.Officer,DateTime.UtcNow.AddDays(3),item.Version);var work=await db.CaseWorkflows.SingleAsync();await service.ActAsync(ids.Officer,item.Id,work.Version,"Accept","Work has started onsite");var code=await service.IssueAsync(ids.Admin,item.Id);var due=work.DueUtc;var version=work.Version;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReassignAsync(ids.Officer,item.Id,replacement.Id,version,"Officer on leave"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReassignAsync(ids.Admin,item.Id,ids.Other,version,"Officer on leave"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReassignAsync(ids.Admin,item.Id,replacement.Id,version-1,"Officer on leave"));
        await service.ReassignAsync(ids.Admin,item.Id,replacement.Id,version,"Original officer is on leave");
        Assert.Equal(due,work.DueUtc);Assert.Equal("InProgress",work.Stage);Assert.NotNull(await service.CitizenAsync(code));Assert.False(await service.CanReadAsync(ids.Officer,item));Assert.True(await service.CanReadAsync(replacement.Id,item));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ActAsync(ids.Officer,item.Id,work.Version,"Progress","Unauthorized old officer update"));
        await service.ActAsync(replacement.Id,item.Id,work.Version,"Progress","Replacement officer continued repairs");
        Assert.Contains(await db.WorkflowUpdates.ToListAsync(),u=>u.Note.Contains("Original officer is on leave"));Assert.True(await db.CaseNotifications.AnyAsync(n=>n.Recipient==replacement.Id.ToString()));
        var original=await db.Operators.SingleAsync(o=>o.Id==ids.Officer);await service.ManageOfficerAsync(ids.Admin,original.Id,original.AccountVersion,"Disable","","","","","","Officer is unavailable");Assert.True(original.IsDisabled);
    }
    [Fact]
    public async Task AccountGuardsVersionsPasswordsAndSessionStamps()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var ids=await TeamWorkflowTests.Seed(sp);var db=sp.GetRequiredService<WorkspaceDb>();var service=sp.GetRequiredService<WorkflowService>();var user=await db.Operators.SingleAsync(o=>o.Id==ids.Officer);var stamp=WorkspaceIdentity.CredentialStamp(user);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ManageOfficerAsync(ids.Reviewer,user.Id,0,"Disable","","","","","","Admin action required"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ManageOfficerAsync(ids.Admin,ids.Admin,0,"Disable","","","","","","Cannot disable admin"));
        await service.ManageOfficerAsync(ids.Admin,user.Id,0,"Disable","","","","","","Officer on leave");Assert.NotEqual(stamp,WorkspaceIdentity.CredentialStamp(user));await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.RoleAsync(user.Id));
        await service.ManageOfficerAsync(ids.Admin,user.Id,1,"Enable","","","","","","Officer returned today");Assert.NotEqual(stamp,WorkspaceIdentity.CredentialStamp(user));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ManageOfficerAsync(ids.Admin,user.Id,0,"Disable","","","","","","Stale admin request"));
        await service.ManageOfficerAsync(ids.Admin,user.Id,2,"Reset","","","","","New-synthetic-password-456","Officer requested reset");Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,WorkspaceIdentity.Hasher.VerifyHashedPassword(user,user.PasswordHash,"New-synthetic-password-456"));
        await service.ManageOfficerAsync(ids.Admin,user.Id,3,"Edit","Renamed officer","renamed@example.test","Department","water","","Corrected account profile");Assert.Equal("renamed@example.test",user.Email);
        await service.AssignAsync(ids.Admin,ids.Case,user.Id,DateTime.UtcNow.AddDays(3),1);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ManageOfficerAsync(ids.Admin,user.Id,4,"Disable","","","","","","Officer leaving team"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ManageOfficerAsync(ids.Admin,user.Id,4,"Edit",user.Name,user.Email,"Reviewer","","","Change officer role"));
        Assert.DoesNotContain(await db.AccountAudits.ToListAsync(),a=>a.Action.Contains("password-456"));
    }
    [Fact]
    public async Task DisabledAccountCannotLoginAndExistingCookieNeverResurrects()
    {
        using var app=new TestApp();Guid admin,target;using(var scope=app.Services.CreateScope()){var ids=await TeamWorkflowTests.Seed(scope.ServiceProvider);admin=ids.Admin;target=ids.Officer;}
        using var browser=app.CreateClient(new(){AllowAutoRedirect=false});Assert.Equal(System.Net.HttpStatusCode.Redirect,(await TeamWorkflowTests.Post(browser,"/Account/Login",new(){["Email"]="water@example.test",["Password"]="Synthetic-password-123"})).StatusCode);
        using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<WorkflowService>().ManageOfficerAsync(admin,target,0,"Disable","","","","","","Officer on leave");
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await browser.GetAsync("/Work")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK,(await TeamWorkflowTests.Post(browser,"/Account/Login",new(){["Email"]="water@example.test",["Password"]="Synthetic-password-123"})).StatusCode);
        using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<WorkflowService>().ManageOfficerAsync(admin,target,1,"Enable","","","","","","Officer returned");
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await browser.GetAsync("/Work")).StatusCode);
    }
}
