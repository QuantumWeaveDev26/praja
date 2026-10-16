using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class PasswordRecoveryTests
{
    [Fact]
    public async Task RecoveryBacksUpDatabaseKeepsCaseAndUnlocksAccountWithNewPassword()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["__RequestVerificationToken"] = await Token(client,"/Setup"), ["Input.Name"] = "Test recovery", ["Input.Email"] = "recovery@example.test", ["Input.Password"] = "Old-Recovery-Password-82!" }));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            var user = await db.Operators.SingleAsync();
            user.FailedLogins = 4; user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(15);
            var item = new Grievance { OwnerId = user.Id, Reference = "RECOVERY-CASE", OriginalText = "Preserve my original petition after recovery." };
            db.Grievances.Add(item); await db.SaveChangesAsync();
            var oldHash = user.PasswordHash;
            var email = await LocalPasswordRecovery.ResetAsync(db, scope.ServiceProvider.GetRequiredService<IHostEnvironment>(), "New-Recovery-Password-82!", Path.Combine(app.SettingsDirectory,"backups"));
            Assert.Equal("recovery@example.test", email);
            Assert.Equal(0, user.FailedLogins); Assert.Null(user.LockedUntilUtc);
            Assert.NotEqual(oldHash,user.PasswordHash);
            Assert.Equal("Preserve my original petition after recovery.",(await db.Grievances.SingleAsync()).OriginalText);
            var backup = Directory.GetFiles(Path.Combine(app.SettingsDirectory,"backups"),"*.db").Single();
            await using var backupDb = new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite($"Data Source={backup};Mode=ReadOnly;Pooling=False").Options);
            Assert.Equal(oldHash,(await backupDb.Operators.SingleAsync()).PasswordHash);
            Assert.Equal(item.Id,(await backupDb.Grievances.SingleAsync()).Id);
        }
        var oldLogin = await Login(client,"Old-Recovery-Password-82!");
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/cases")).StatusCode);
        var newLogin = await Login(client,"New-Recovery-Password-82!");
        Assert.Equal(HttpStatusCode.Redirect,newLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/cases")).StatusCode);
    }

    [Fact]
    public async Task InvalidPasswordCannotChangeAccountOrCreateBackup()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        await client.GetAsync("/Citizen");
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var user = new Operator { Email="recovery-invalid@example.test",PasswordHash="unchanged" };
        db.Operators.Add(user); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalPasswordRecovery.ResetAsync(db,scope.ServiceProvider.GetRequiredService<IHostEnvironment>(),"short",app.SettingsDirectory));
        Assert.Equal("unchanged",user.PasswordHash); Assert.False(Directory.Exists(app.SettingsDirectory));
    }
    private static async Task<HttpResponseMessage> Login(HttpClient client,string password) => await client.PostAsync("/Account/Login",new FormUrlEncodedContent(new Dictionary<string,string>
    { ["__RequestVerificationToken"] = await Token(client,"/Account/Login"), ["Email"]="recovery@example.test", ["Password"]=password }));
    [Fact]
    public async Task ProductionRecoveryIsRejectedBeforeAnyChange()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        await client.GetAsync("/Citizen");
        using var scope = app.Services.CreateScope();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        environment.EnvironmentName = "Production";
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalPasswordRecovery.ResetAsync(db,environment,"Production-Test-Password-72!",app.SettingsDirectory));
        Assert.False(Directory.Exists(app.SettingsDirectory));
    }
    private static async Task<string> Token(HttpClient client,string path) => WebUtility.HtmlDecode(Regex.Match(await client.GetStringAsync(path),"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
}
