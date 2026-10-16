using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Xunit;

namespace Praja.Tests;
public class InboxFlowTests
{
    private const string Complaint = "No water supply in Ward 12, Mysuru, for three days. Please restore supply.";
    [Fact]
    public async Task CapturedSocialMessageBecomesOneDraftWithProvenance()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() {AllowAutoRedirect=false}); await Setup(client);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/Intake")).StatusCode);
        var captured=await Post(client,"/Intake/New",new() {
            ["Input.OriginalText"]=Complaint,["Input.Channel"]="Social media",["Input.Language"]="English",
            ["Input.SourceUrl"]="https://social.example.test/posts/123",["Input.ExternalId"]="post-123",["Input.Permission"]="true"
        });
        Assert.Equal(HttpStatusCode.Redirect,captured.StatusCode); var detail=captured.Headers.Location!.ToString();var id=detail.Split('/').Last();
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        Assert.Empty(await db.Grievances.ToListAsync());
        var html=await client.GetStringAsync(detail);Assert.Contains("social.example.test/posts/123",html);
        var newPath="/Grievances/New?InboxId="+id;
        Assert.Contains("No water supply",await client.GetStringAsync(newPath));
        var draft=await Post(client,newPath,new() {
            ["InboxId"]=id,["InboxVersion"]="1",["Input.OriginalText"]=Complaint,["Input.Language"]="English",["Input.Source"]="Social media",
            ["Input.District"]="Mysuru",["Input.Location"]="Ward 12",["Input.Consent"]="true"
        });
        Assert.Equal(HttpStatusCode.Redirect,draft.StatusCode);
        var item=await db.Grievances.Include(g=>g.History).SingleAsync();Assert.Equal("Draft",item.Status);Assert.Equal("Social media",item.Source);Assert.Equal(Complaint,item.OriginalText);
        Assert.Contains(item.History,h=>h.Action.Contains("inbox",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(item.Id.ToString(),await client.GetStringAsync(detail));
        var repeat=await client.GetAsync(newPath);Assert.Equal(HttpStatusCode.Redirect,repeat.StatusCode);
        Assert.Equal(1,await db.Grievances.CountAsync());
    }

    [Fact]
    public async Task RepeatedSourceIdIsIdempotentAndChangedContentIsRejected()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        var fields=new Dictionary<string,string>{["Input.OriginalText"]=Complaint,["Input.Channel"]="Field meeting",["Input.Language"]="English",["Input.ExternalId"]="meeting-12",["Input.Permission"]="true"};
        var first=await Post(client,"/Intake/New",fields);var retry=await Post(client,"/Intake/New",fields);
        Assert.Equal(HttpStatusCode.Redirect,first.StatusCode);Assert.Equal(first.Headers.Location,retry.Headers.Location);
        fields["Input.OriginalText"]="Garbage remains uncollected in Ward 12 for a week. Please clear it.";
        Assert.Equal(HttpStatusCode.OK,(await Post(client,"/Intake/New",fields)).StatusCode);
    }

    [Fact]
    public async Task MatchingRecentCaseIsFlaggedBeforeConversion()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        using(var scope=app.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();
            db.Grievances.Add(new(){OwnerId=owner.Id,Reference="DUPLICATE-CANDIDATE",OriginalText=Complaint,Title="Water supply complaint"});await db.SaveChangesAsync();
        }
        var response=await Post(client,"/Intake/New",new(){["Input.OriginalText"]=Complaint,["Input.Channel"]="Paper transcription",["Input.Language"]="English",["Input.Permission"]="true"});
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);
        var html=await client.GetStringAsync(response.Headers.Location!);Assert.Contains("DUPLICATE-CANDIDATE",html);Assert.Contains("Matching wording",html);
    }

    [Fact]
    public async Task InboxCannotBeUsedToReplacePreservedSourceText()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        var captured=await Post(client,"/Intake/New",new(){["Input.OriginalText"]=Complaint,["Input.Channel"]="Text",["Input.Language"]="English",["Input.Permission"]="true"});
        Assert.Equal(HttpStatusCode.Redirect,captured.StatusCode);var id=captured.Headers.Location!.ToString().Split('/').Last();
        var response=await Post(client,"/Grievances/New?InboxId="+id,new(){["InboxId"]=id,["InboxVersion"]="1",["Input.OriginalText"]="Invented different complaint about garbage in another place.",["Input.Language"]="English",["Input.Source"]="Text",["Input.District"]="Mysuru",["Input.Location"]="Ward 12",["Input.Consent"]="true"});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var scope=app.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
    }
    private static async Task Setup(HttpClient client)=>Assert.Equal(HttpStatusCode.Redirect,(await Post(client,"/Setup",new(){["Input.Name"]="Inbox reviewer",["Input.Email"]="inbox@example.test",["Input.Password"]="Inbox-Test-Password-72!"})).StatusCode);
    private static async Task<HttpResponseMessage> Post(HttpClient client,string path,Dictionary<string,string> fields){
        var page=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,page.StatusCode);var html=await page.Content.ReadAsStringAsync();
        fields["__RequestVerificationToken"]=WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        return await client.PostAsync(path,new FormUrlEncodedContent(fields));
    }
}
