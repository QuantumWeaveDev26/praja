using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Xunit;

namespace Praja.Tests;
public class IntakeAndAccessFlowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AsyncVoiceReturnsReviewableJsonWithoutCreatingCase(bool consent)
    {
        using var app = new TestApp(new VoiceReply()); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(await Token(client,"/Grievances/New")), "__RequestVerificationToken");
        content.Add(new StringContent("English"), "Input.Language");
        content.Add(new StringContent(consent ? "true" : "false"), "Input.Consent");
        content.Add(new ByteArrayContent("RIFF0123WAVE012345678901234567890123"u8.ToArray()), "Audio", "recording.wav");
        using var request = new HttpRequestMessage(HttpMethod.Post,"/Grievances/New?handler=Audio") { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(request);
        Assert.Equal(consent ? HttpStatusCode.OK : HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Equal("application/json",response.Content.Headers.ContentType!.MediaType);
        using var reply = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (consent) Assert.Contains("Water supply",reply.RootElement.GetProperty("text").GetString());
        else Assert.Contains("consent",reply.RootElement.GetProperty("error").GetString());
        using var scope=app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
    }
    private sealed class VoiceReply : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Water supply has stopped in Ward 12 for five days.\"}") });
    }
    [Fact]
    public async Task DocumentExtractionWaitsForReviewAndThenRegistersThroughRenderedForm()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client);
        var csrf = await Token(client, "/Grievances/New");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(csrf), "__RequestVerificationToken");
        content.Add(new StringContent("English"), "Input.Language");
        content.Add(new StringContent("true"), "Input.Consent");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Water supply has stopped in Ward 12 for five days.")), "Document", "petition.txt");
        var response = await client.PostAsync("/Grievances/New?handler=Document", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Water supply has stopped", html);
        using var scope = app.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.CountAsync());
        // Follow the rendered form's destination, just as the browser does after extraction.
        var form = Regex.Match(html, "<form[^>]*id=\"intake-form\"[^>]*>").Value;
        var action = WebUtility.HtmlDecode(Regex.Match(form,"action=\"([^\"]+)\"").Groups[1].Value);
        if (action.Length == 0) action = "/Grievances/New?handler=Document";
        var draft = await client.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value),
            ["Input.OriginalText"] = "Water supply has stopped in Ward 12 for five days.", ["Input.District"]="Bengaluru Urban", ["Input.Location"]="Ward 12",
            ["Input.Language"]="English", ["Input.Source"]="Document extraction", ["Input.Consent"]="true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, draft.StatusCode);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.CountAsync());
    }

    [Fact]
    public async Task OfficerCanIssueTrackedAccessForRegisteredCaseButNotDraft()
    {
        using var app = new TestApp(); using var officer = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(officer);
        Guid registeredId, draftId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
            var registered = new Grievance { OwnerId=owner.Id, Reference="ACCESS-REGISTERED", Title="Water service interrupted", Status="Registered", Department="water", OriginalText="PRIVATE-ORIGINAL-DETAILS", DraftText="PRIVATE-DRAFT-DETAILS", EvidenceReference="PRIVATE-EVIDENCE" };
            var draft = new Grievance { OwnerId=owner.Id, Reference="ACCESS-DRAFT", Title="Draft complaint", Status="Draft" };
            db.Grievances.AddRange(registered,draft); await db.SaveChangesAsync(); registeredId=registered.Id; draftId=draft.Id;
        }
        var registeredPath="/Grievances/Details/"+registeredId;
        var shared=await officer.PostAsync(registeredPath+"?handler=Share", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["__RequestVerificationToken"]=await Token(officer,registeredPath), ["Version"]="1" }));
        Assert.Equal(HttpStatusCode.OK,shared.StatusCode);
        var html=await shared.Content.ReadAsStringAsync();
        var code=WebUtility.HtmlDecode(Regex.Match(html,"<textarea[^>]*id=\"citizen-access-code\"[^>]*>(.*?)</textarea>",RegexOptions.Singleline).Groups[1].Value).Trim();
        Assert.NotEmpty(code);
        using var citizen=app.CreateClient(new() { AllowAutoRedirect=false });
        var status=await citizen.PostAsync("/Citizen?handler=Track",new FormUrlEncodedContent(new Dictionary<string,string>
        { ["__RequestVerificationToken"]=await Token(citizen,"/Citizen"), ["Language"]="English", ["AccessCode"]=code }));
        var publicHtml=await status.Content.ReadAsStringAsync();
        Assert.Contains("ACCESS-REGISTERED",publicHtml);
        Assert.DoesNotContain("PRIVATE-ORIGINAL-DETAILS",publicHtml);
        Assert.DoesNotContain(code,publicHtml);
        var draftPath="/Grievances/Details/"+draftId;
        var denied=await officer.PostAsync(draftPath+"?handler=Share",new FormUrlEncodedContent(new Dictionary<string,string>
        { ["__RequestVerificationToken"]=await Token(officer,draftPath), ["Version"]="1" }));
        Assert.Equal(HttpStatusCode.BadRequest,denied.StatusCode);
        using var scope2=app.Services.CreateScope();
        Assert.Contains(await scope2.ServiceProvider.GetRequiredService<WorkspaceDb>().AuditEntries.ToListAsync(),h=>h.Action.Contains("citizen access",StringComparison.OrdinalIgnoreCase));
    }
    private static async Task Setup(HttpClient client)
    {
        var response=await client.PostAsync("/Setup",new FormUrlEncodedContent(new Dictionary<string,string>
        { ["__RequestVerificationToken"]=await Token(client,"/Setup"), ["Input.Name"]="Test officer", ["Input.Email"]="intake@example.test", ["Input.Password"]="Intake-Test-Password-72!" }));
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);
    }
    private static async Task<string> Token(HttpClient client,string path)
    {
        var html=await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }
}
