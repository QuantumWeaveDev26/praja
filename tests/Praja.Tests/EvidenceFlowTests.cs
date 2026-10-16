using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;

namespace Praja.Tests;

public class EvidenceFlowTests
{
    [Fact]
    public async Task EvidenceEndpointRequiresOfficerAuthentication()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync($"/Grievances/Evidence/{Guid.NewGuid()}?attachmentId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login",response.Headers.Location!.ToString());
    }
    [Fact]
    public async Task OfficerUploadIsPrivateDownloadAndCitizenStatusDoesNotRevealEvidence()
    {
        using var app=new TestApp(); using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        await Setup(client); Guid caseId;
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner=await db.Operators.SingleAsync();
            var item=new Grievance {OwnerId=owner.Id,Reference="EVIDENCE-FLOW",Title="Water supply grievance",OriginalText="Original water concern in sample ward.",Status="Registered",Department="water",District="Bengaluru Urban"};
            db.Grievances.Add(item); await db.SaveChangesAsync(); caseId=item.Id;
        }
        var bytes="%PDF-1.7\nPRIVATE SYNTHETIC EVIDENCE BODY\n%%EOF"u8.ToArray();
        using var body=new MultipartFormDataContent(); body.Add(new StringContent(await Token(client,$"/Grievances/Details/{caseId}")),"__RequestVerificationToken");
        body.Add(new StringContent("1"),"Version"); body.Add(new StringContent("true"),"EvidenceConsent"); body.Add(new StringContent("<script>private description</script>"),"Description"); body.Add(new ByteArrayContent(bytes),"EvidenceFile","inspection.pdf");
        var upload=await client.PostAsync($"/Grievances/Evidence/{caseId}",body); Assert.Equal(HttpStatusCode.Redirect,upload.StatusCode);
        var html=await client.GetStringAsync($"/Grievances/Details/{caseId}");
        Assert.Contains("Download inspection.pdf",html); Assert.Contains("&lt;script&gt;private description&lt;/script&gt;",html); Assert.DoesNotContain("<script>private description</script>",html);
        Guid attachmentId; string code;
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var record=await db.EvidenceAttachments.SingleAsync(); attachmentId=record.Id;
            Assert.False(bytes.AsSpan().SequenceEqual(record.ProtectedContent)); Assert.Equal(2,(await db.Grievances.SingleAsync()).Version); Assert.Single(await db.AuditEntries.ToListAsync());
            code=(await scope.ServiceProvider.GetRequiredService<CitizenAccessService>().IssueAsync(caseId,(await db.Operators.SingleAsync()).Id))!.Code;
        }
        var download=await client.GetAsync($"/Grievances/Evidence/{caseId}?attachmentId={attachmentId}");
        Assert.Equal(HttpStatusCode.OK,download.StatusCode); Assert.Equal("application/octet-stream",download.Content.Headers.ContentType!.MediaType); Assert.Equal("attachment",download.Content.Headers.ContentDisposition!.DispositionType); Assert.Equal(bytes,await download.Content.ReadAsByteArrayAsync());
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});
        var tracked=await citizen.PostAsync("/Citizen?handler=Track",new FormUrlEncodedContent(new Dictionary<string,string>{["__RequestVerificationToken"]=await Token(citizen,"/Citizen"),["AccessCode"]=code,["Language"]="English"}));
        var publicHtml=await tracked.Content.ReadAsStringAsync(); Assert.Contains("EVIDENCE-FLOW",publicHtml); Assert.DoesNotContain("inspection.pdf",publicHtml); Assert.DoesNotContain("private description",publicHtml); Assert.DoesNotContain("PRIVATE SYNTHETIC",publicHtml);
    }
    [Fact]
    public async Task EvidenceUploadWithoutCsrfIsRejected()
    {
        using var app=new TestApp(); using var client=app.CreateClient(new(){AllowAutoRedirect=false}); await Setup(client);
        using var body=new MultipartFormDataContent(); body.Add(new ByteArrayContent("%PDF-1.7\nfile"u8.ToArray()),"EvidenceFile","file.pdf");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync($"/Grievances/Evidence/{Guid.NewGuid()}",body)).StatusCode);
    }
    private static async Task Setup(HttpClient client)=>await client.PostAsync("/Setup",new FormUrlEncodedContent(new Dictionary<string,string>{["__RequestVerificationToken"]=await Token(client,"/Setup"),["Input.Name"]="Test officer",["Input.Email"]="evidence-flow@example.test",["Input.Password"]="Evidence-Flow-Test-2026!"}));
    private static async Task<string> Token(HttpClient client,string path)=>WebUtility.HtmlDecode(Regex.Match(await client.GetStringAsync(path),"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
}
