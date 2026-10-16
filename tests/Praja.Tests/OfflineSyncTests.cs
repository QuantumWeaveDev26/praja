using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;
namespace Praja.Tests;
public class OfflineSyncTests
{
    private static async Task<HttpResponseMessage> Sync(HttpClient client,Dictionary<string,string> fields)
    {
        var token=System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/OfflineSync")).RootElement.GetProperty("token").GetString()!;
        fields["__RequestVerificationToken"]=token;return await client.PostAsync("/OfflineSync",new FormUrlEncodedContent(fields));
    }
    [Fact]
    public async Task SyncedComplaintRetryCreatesOneCaseThenCompletesThroughCitizenApproval()
    {
        using var app=new TestApp();Guid officerId;
        using(var scope=app.Services.CreateScope()){
            var ids=await Seed(scope.ServiceProvider);officerId=ids.Officer;
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var admin=await db.Operators.SingleAsync(o=>o.Id==ids.Admin);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");await db.SaveChangesAsync();
        }
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});
        var key=Guid.NewGuid().ToString();var fields=new Dictionary<string,string>{["SubmissionKey"]=key,["Original"]="Fictional test: no water supply in Ward 12, Mysuru, for three days.",["District"]="Mysuru",["Location"]="Ward 12",["Language"]="English",["Consent"]="true",["Sample"]="true"};
        var first=await Sync(citizen,fields);Assert.Equal(System.Net.HttpStatusCode.OK,first.StatusCode);
        var receipt=System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync());var code=receipt.RootElement.GetProperty("code").GetString()!;
        var retry=await Sync(citizen,fields);var repeated=System.Text.Json.JsonDocument.Parse(await retry.Content.ReadAsStringAsync());Assert.Equal(receipt.RootElement.GetProperty("reference").GetString(),repeated.RootElement.GetProperty("reference").GetString());
        Guid id;using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var savedReceipt=await db.CitizenReceipts.SingleAsync();id=savedReceipt.Id;Assert.Equal(2,await db.Grievances.CountAsync());}
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

    [Fact]
    public async Task SyncRequiresCsrfConsentAndValidData()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();await Seed(scope.ServiceProvider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,(await client.PostAsync("/OfflineSync",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var fields=new Dictionary<string,string>{["SubmissionKey"]=Guid.NewGuid().ToString(),["Original"]="Fictional water complaint in Mysuru",["District"]="Mysuru",["Location"]="Ward 12",["Language"]="English",["Consent"]="false"};
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,(await Sync(client,fields)).StatusCode);Assert.Single(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
        Assert.Contains("Offline capture",await client.GetStringAsync("/Submit"));Assert.Contains("Save a complaint offline",await client.GetStringAsync("/offline.html"));
    }
}
