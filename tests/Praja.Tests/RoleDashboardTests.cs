using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
namespace Praja.Tests;

public class RoleDashboardTests
{
    [Theory]
    [InlineData("admin@example.test", "Admin dashboard", "Open")]
    [InlineData("intake@example.test", "Intake dashboard", "Unassigned")]
    [InlineData("water@example.test", "My department work", "Active")]
    [InlineData("review@example.test", "Verification dashboard", "Verification")]
    public async Task LoginLandsOnRoleDashboardWithCorrectDefaultQueue(string email,string heading,string filter)
    {
        using var app=new TestApp();
        using(var scope=app.Services.CreateScope()){
            var ids=await TeamWorkflowTests.Seed(scope.ServiceProvider);
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            var admin=await db.Operators.SingleAsync(o=>o.Id==ids.Admin);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<WorkflowService>().CreateOfficerAsync(ids.Admin,"Intake officer","intake@example.test","Synthetic-password-123","Intake","");
            foreach(var account in await db.Operators.ToListAsync())account.MustChangePassword=false;await db.SaveChangesAsync();
        }
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        var login=await TeamWorkflowTests.Post(client,"/Account/Login",new(){["Email"]=email,["Password"]="Synthetic-password-123"});
        Assert.Equal("/Work",login.Headers.Location?.ToString());
        var page=await client.GetStringAsync("/Work");Assert.Contains(heading,page);
        Assert.Contains("data-count=\""+filter+"\"",page);
        if(filter is "Open" or "Unassigned")Assert.Contains("Water complaint",page);else Assert.DoesNotContain("Water complaint",page);
        Assert.Equal(email.StartsWith("admin"),page.Contains("Manage officers"));
    }

    [Fact]
    public async Task DepartmentCountsFiltersAndDirectCaseAccessStayScoped()
    {
        using var app=new TestApp();Guid hidden;
        using(var scope=app.Services.CreateScope()){
            var ids=await TeamWorkflowTests.Seed(scope.ServiceProvider);var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            await scope.ServiceProvider.GetRequiredService<WorkflowService>().AssignAsync(ids.Admin,ids.Case,ids.Officer,DateTime.UtcNow.AddDays(3),1);
            var other=new Grievance{OwnerId=ids.Admin,Reference="PRIVATE-OTHER",Title="Hidden other department complaint",Department="sanitation",Status="Registered"};hidden=other.Id;db.Grievances.Add(other);await db.SaveChangesAsync();
        }
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});await TeamWorkflowTests.Post(client,"/Account/Login",new(){["Email"]="water@example.test",["Password"]="Synthetic-password-123"});
        foreach(var filter in new[]{"All","Active","Assigned","Unassigned","malformed"}){
            var page=await client.GetStringAsync("/Work?filter="+filter);Assert.Contains("Water complaint",page);Assert.DoesNotContain("Hidden other",page);Assert.Contains("data-count=\"All\">1</strong>",page);
        }
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/Work/Case/"+hidden)).StatusCode);
        Assert.DoesNotContain("Water complaint",await client.GetStringAsync("/Work?filter=Closed"));
    }

    [Fact]
    public async Task ReviewerDefaultOnlyShowsVerificationAndCountsBeyondPageSize()
    {
        using var app=new TestApp();
        using(var scope=app.Services.CreateScope()){
            var ids=await TeamWorkflowTests.Seed(scope.ServiceProvider);var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            for(var i=0;i<35;i++){
                var item=new Grievance{OwnerId=ids.Admin,Reference=$"VERIFY-{i:00}",Title=$"Verification item {i:00}",Department="water",Status="ClosureReview"};
                db.Grievances.Add(item);db.CaseWorkflows.Add(new(){Id=item.Id,AssignedOfficerId=ids.Officer,Stage="Verification",DueUtc=DateTime.UtcNow.Date.AddDays(i)});
            }
            await db.SaveChangesAsync();
        }
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});await TeamWorkflowTests.Post(client,"/Account/Login",new(){["Email"]="review@example.test",["Password"]="Synthetic-password-123"});
        var first=await client.GetStringAsync("/Work");Assert.Contains("data-count=\"Verification\">35</strong>",first);Assert.Contains("Verification item 00",first);Assert.DoesNotContain("Verification item 34",first);Assert.DoesNotContain("Water complaint",first);
        var second=await client.GetStringAsync("/Work?pageNumber=2");Assert.Contains("Verification item 34",second);Assert.DoesNotContain("Verification item 00",second);
        Assert.Contains("Page 2 of 2",await client.GetStringAsync("/Work?pageNumber=99999"));
        Assert.Contains("Page 1 of 2",await client.GetStringAsync("/Work?pageNumber=-2"));
    }
}
