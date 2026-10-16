using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
namespace Praja.Tests;
public class CitizenPortalTests
{
    [Fact]
    public async Task PublicPortalNeverShowsStaffNavigationEvenInOfficerSession()
    {
        using var app=new TestApp();using(var scope=app.Services.CreateScope()){await TeamWorkflowTests.Seed(scope.ServiceProvider);}
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal("/Submit",(await client.GetAsync("/")).Headers.Location?.ToString());
        foreach(var url in new[]{"/Work","/Work/Team","/Automation","/AiSettings","/GmailSettings"})Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync(url)).StatusCode);
        await TeamWorkflowTests.Post(client,"/Account/Login",new(){["Email"]="water@example.test",["Password"]="Synthetic-password-123"});
        foreach(var url in new[]{"/Submit","/submit/","/Track","/Transparency","/CitizenLogin","/PublicMap"}){
            var html=await client.GetStringAsync(url);Assert.DoesNotContain("Main navigation",html);Assert.DoesNotContain("Manage officers",html);Assert.DoesNotContain("My dashboard",html);Assert.Contains("CITIZEN PORTAL",html);
        }
        var response=await client.GetAsync("/Submit");Assert.Contains("microphone=(self)",response.Headers.GetValues("Permissions-Policy").Single());
        Assert.Contains("geolocation=(self)",response.Headers.GetValues("Permissions-Policy").Single());
        var map=await client.GetAsync("/PublicMap");Assert.Contains("geolocation=(self)",map.Headers.GetValues("Permissions-Policy").Single());
        Assert.Contains("https://tile.openstreetmap.org",map.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin",map.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task VoiceTranscriptionRegistersComplaintWithoutGivingStaffAccess()
    {
        var provider=new SpeechReply();using var app=new TestApp(provider);
        using(var scope=app.Services.CreateScope()){await TeamWorkflowTests.Seed(scope.ServiceProvider);}
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});
        var transcriptResponse=await Audio(citizen,true);
        Assert.Equal(HttpStatusCode.OK,transcriptResponse.StatusCode);Assert.Equal(1,provider.Calls);
        var transcript=JsonDocument.Parse(await transcriptResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("text").GetString()!;
        var submitted=await TeamWorkflowTests.Post(citizen,"/Submit",new(){["SubmissionKey"]=Guid.NewGuid().ToString(),["Original"]=transcript,["District"]="Mysuru",["Location"]="Ward 12",["Language"]="English",["Consent"]="true",["Sample"]="true",["Source"]="Voice transcript"});
        Assert.Contains("Complaint received",await submitted.Content.ReadAsStringAsync());
        using var final=app.Services.CreateScope();var item=await final.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync(g=>g.Source=="Voice transcript");Assert.Equal(transcript,item.OriginalText);
        Assert.Equal(HttpStatusCode.Redirect,(await citizen.GetAsync("/Work")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await citizen.GetAsync("/api/cases")).StatusCode);
    }

    [Fact]
    public async Task SpeechRequiresConsentAntiforgeryAndValidAudioBeforeCallingProvider()
    {
        var provider=new SpeechReply();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(HttpStatusCode.BadRequest,(await Audio(client,false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Audio(client,true,token:false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Audio(client,true,bytes:"not audio"u8.ToArray())).StatusCode);
        Assert.Equal(0,provider.Calls);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await Audio(client,true)).StatusCode);
    }

    [Fact]
    public async Task SpeechFailureIsRecoverableAndOtpNeverPretendsToBeConnected()
    {
        using var app=new TestApp(new SpeechReply(fail:true));using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        var response=await Audio(client,true);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Contains("could not process",await response.Content.ReadAsStringAsync());
        var login=await client.GetStringAsync("/CitizenLogin");Assert.Contains("not connected yet",login);Assert.DoesNotContain("type=\"tel\"",login);
        using var scope=app.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
    }

    private static async Task<HttpResponseMessage> Audio(HttpClient client,bool consent,bool token=true,byte[]? bytes=null)
    {
        var html=await client.GetStringAsync("/Submit");var csrf=WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        using var body=new MultipartFormDataContent();body.Add(new ByteArrayContent(bytes??"RIFF0123WAVE012345678901234567890123"u8.ToArray()),"Audio","voice.wav");body.Add(new StringContent("English"),"Language");body.Add(new StringContent(consent?"true":"false"),"VoiceConsent");if(token)body.Add(new StringContent(csrf),"__RequestVerificationToken");
        return await client.PostAsync("/CitizenVoice?handler=Audio",body);
    }
    private sealed class SpeechReply(bool fail=false):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){Calls++;return Task.FromResult(new HttpResponseMessage(fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent("{\"text\":\"Fictional complaint: No water supply in Ward 12 for three days.\"}",System.Text.Encoding.UTF8,"application/json")});}
    }
}
