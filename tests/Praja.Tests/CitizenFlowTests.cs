using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class CitizenFlowTests
{
    [Fact]
    public async Task CitizenGuidanceIsPublicAndDoesNotCollectAComplaint()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync("/Citizen");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var reply = await client.PostAsync("/Citizen?handler=Guide", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Language"] = "English", ["Topic"] = "category", ["Concern"] = "No drinking water in Ward 12"
        }));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("Water supply", await reply.Content.ReadAsStringAsync());
        using var scope = app.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.CountAsync());
    }

    [Fact]
    public async Task AnonymousTrackingPostRequiresCsrf()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.PostAsync("/Citizen?handler=Track", new FormUrlEncodedContent(new Dictionary<string, string> { ["AccessCode"] = "invalid" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TrackingCodeIsAcceptedOnlyByPostAndNeverEchoedOrIncludesPrivateCaseText()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        string code;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            var owner = new Operator { Email = "citizen-test@example.test" };
            var item = new Grievance
            {
                OwnerId = owner.Id, Reference = "PRJ-CITIZEN-ONLY", Title = "<script>public title</script>", Status = "Registered", Department = "water", District = "Bengaluru Urban",
                OriginalText = "PRIVATE narrative", DraftText = "PRIVATE draft", EvidenceReference = "PRIVATE evidence", ResolutionReport = "PRIVATE report", Location = "PRIVATE location"
            };
            db.Operators.Add(owner); db.Grievances.Add(item); await db.SaveChangesAsync();
            code = (await scope.ServiceProvider.GetRequiredService<CitizenAccessService>().IssueAsync(item.Id, owner.Id))!.Code;
        }
        var get = await client.GetAsync($"/Citizen?handler=Track&AccessCode={Uri.EscapeDataString(code)}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var html = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PRJ-CITIZEN-ONLY", html);
        Assert.DoesNotContain(code, html);
        var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var queryPost = await client.PostAsync($"/Citizen?handler=Track&AccessCode={Uri.EscapeDataString(code)}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["Language"] = "English"
        }));
        Assert.DoesNotContain("PRJ-CITIZEN-ONLY", await queryPost.Content.ReadAsStringAsync());
        var post = await client.PostAsync("/Citizen?handler=Track", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["AccessCode"] = code, ["Language"] = "English"
        }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        html = await post.Content.ReadAsStringAsync();
        Assert.Contains("PRJ-CITIZEN-ONLY", html);
        Assert.Contains("&lt;script&gt;public title&lt;/script&gt;", html);
        Assert.DoesNotContain("<li>The Topic field is required.</li>", html);
        Assert.DoesNotContain("<li>The Language field is required.</li>", html);
        foreach (var secret in new[] { "PRIVATE narrative", "PRIVATE draft", "PRIVATE evidence", "PRIVATE report", "PRIVATE location" })
            Assert.DoesNotContain(secret, html);
        Assert.DoesNotContain(code, html);
    }

    [Fact]
    public async Task KannadaGuidanceUsesRulesAndSafelyRetainsTheEnteredConcern()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var html = await client.GetStringAsync("/Citizen");
        var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var response = await client.PostAsync("/Citizen?handler=Guide", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["Language"] = "Kannada", ["Topic"] = "category", ["Concern"] = "ನೀರಿಲ್ಲ <script>alert(1)</script>"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        html = await response.Content.ReadAsStringAsync();
        Assert.Contains("ನೀರಿನ ಸರಬರಾಜು", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }
}
