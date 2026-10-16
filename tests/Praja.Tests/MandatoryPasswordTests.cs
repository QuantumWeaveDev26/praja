using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;
using System.Net;
namespace Praja.Tests;
public class MandatoryPasswordTests
{
    private const string Temporary="Temporary-password-123";
    private const string Permanent="Personal-new-password-456";
    private static async Task<(Guid Admin,Guid Officer)> Create(TestApp app)
    {
        using var scope=app.Services.CreateScope();var ids=await Seed(scope.ServiceProvider);var service=scope.ServiceProvider.GetRequiredService<WorkflowService>();
        await service.CreateOfficerAsync(ids.Admin,"New officer","new@example.test",Temporary,"Department","water");
        var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();return(ids.Admin,(await db.Operators.SingleAsync(o=>o.Email=="new@example.test")).Id);
    }
    private static Task<HttpResponseMessage> Login(HttpClient client,string password=Temporary)=>Post(client,"/Account/Login?returnUrl=%2FWork",new(){["Email"]="new@example.test",["Password"]=password});
    private static Task<HttpResponseMessage> Change(HttpClient client,string current=Temporary,string next=Permanent,string? confirm=null)=>Post(client,"/Account/ChangePassword",new(){["CurrentPassword"]=current,["NewPassword"]=next,["ConfirmPassword"]=confirm??next});
    [Fact]
    public async Task FirstLoginBlocksAllWorkUntilPasswordChangesAndRevokesOtherSessions()
    {
        using var app=new TestApp();var ids=await Create(app);using var first=app.CreateClient(new(){AllowAutoRedirect=false});using var second=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal("/Account/ChangePassword",(await Login(first)).Headers.Location?.OriginalString);await Login(second);
        foreach(var path in new[]{"/Work","/Work/Team","/Grievances","/Account/Login","/Submit","/account/changepassword/../Login"})Assert.Equal("/Account/ChangePassword",(await first.GetAsync(path)).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Forbidden,(await first.GetAsync("/api/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await first.PostAsync("/Work/Team",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var page=await first.GetStringAsync("/Account/ChangePassword");Assert.Contains("Set your own password",page);Assert.DoesNotContain("My dashboard",page);
        Assert.Equal(HttpStatusCode.BadRequest,(await first.PostAsync("/Account/ChangePassword",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        Assert.Equal("/Work",(await Change(first)).Headers.Location?.OriginalString);Assert.Equal(HttpStatusCode.OK,(await first.GetAsync("/Work")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await second.GetAsync("/Work")).StatusCode);
        using var check=app.Services.CreateScope();var db=check.ServiceProvider.GetRequiredService<WorkspaceDb>();var user=await db.Operators.SingleAsync(o=>o.Id==ids.Officer);Assert.False(user.MustChangePassword);Assert.Equal(1,user.AccountVersion);Assert.Contains(await db.AccountAudits.ToListAsync(),a=>a.TargetId==ids.Officer&&a.Action.Contains("mandatory password change"));Assert.DoesNotContain(await db.AccountAudits.ToListAsync(),a=>a.Action.Contains(Permanent));
        using var fresh=app.CreateClient(new(){AllowAutoRedirect=false});Assert.Equal(HttpStatusCode.OK,(await Login(fresh)).StatusCode);Assert.Equal("/Work",(await Login(fresh,Permanent)).Headers.Location?.OriginalString);
    }
    [Fact]
    public async Task RepeatedWrongCurrentPasswordsLockAccountAndThrottleFurtherAttempts()
    {
        using var app=new TestApp();var ids=await Create(app);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Login(client);
        for(var attempt=0;attempt<5;attempt++)Assert.Equal(HttpStatusCode.OK,(await Change(client,"Incorrect-temporary-password")).StatusCode);
        using var scope=app.Services.CreateScope();var user=await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync(o=>o.Id==ids.Officer);Assert.True(user.LockedUntilUtc>DateTime.UtcNow);Assert.True(user.MustChangePassword);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await Change(client)).StatusCode);
    }
    [Theory]
    [InlineData("wrong-password-123","Personal-new-password-456","Personal-new-password-456")]
    [InlineData(Temporary,"short","short")]
    [InlineData(Temporary,Permanent,"different-password-789")]
    [InlineData(Temporary,Temporary,Temporary)]
    public async Task InvalidChangesKeepRestrictionAndNeverEchoPasswords(string current,string next,string confirmation)
    {
        using var app=new TestApp();var ids=await Create(app);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Login(client);
        var response=await Change(client,current,next,confirmation);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var html=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("value=\""+current+"\"",html);Assert.DoesNotContain("value=\""+next+"\"",html);
        Assert.Equal("/Account/ChangePassword",(await client.GetAsync("/Work")).Headers.Location?.OriginalString);using var scope=app.Services.CreateScope();Assert.True((await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync(o=>o.Id==ids.Officer)).MustChangePassword);
    }
    [Fact]
    public async Task AdminResetRequiresAnotherChangeAndLogoutAndAnonymousProtectionWork()
    {
        using var app=new TestApp();var ids=await Create(app);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Login(client);await Change(client);
        using(var scope=app.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<WorkflowService>().ManageOfficerAsync(ids.Admin,ids.Officer,1,"Reset","","","","","Reset-temporary-password-789","Officer requested recovery");
        Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync("/Work")).StatusCode);Assert.Equal("/Account/ChangePassword",(await Login(client,"Reset-temporary-password-789")).Headers.Location?.OriginalString);
        var page=await client.GetStringAsync("/Account/ChangePassword");var token=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(page,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.Equal(HttpStatusCode.Redirect,(await client.PostAsync("/Account/Logout",new FormUrlEncodedContent(new Dictionary<string,string>{["__RequestVerificationToken"]=token}))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync("/Account/ChangePassword")).StatusCode);
    }
}
