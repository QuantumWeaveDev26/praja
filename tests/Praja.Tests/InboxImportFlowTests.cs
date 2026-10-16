using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class InboxImportFlowTests
{
    private const string Header = "channel,external_id,language,source_url,message\n";
    private const string Water = "No water supply in Ward 12 for three days. Please restore supply.";
    private const string Csv = Header + "Email,email-1,English,," + Water + "\n"
        + "Field meeting,field-1,English,,Garbage remains uncollected in Ward 12. Please clear it.\n"
        + "Social media,post-1,Kannada,https://social.example.test/posts/1,ಶಾಲೆಯಲ್ಲಿ ಶಿಕ್ಷಕರ ಕೊರತೆ ಇದೆ. ದಯವಿಟ್ಟು ಶಿಕ್ಷಕರನ್ನು ನೇಮಿಸಿ.\n";

    [Fact]
    public async Task PreviewIsReadOnlyAndConfirmedRetryReusesAllThreeChannels()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var html = await Preview(client, Csv);
        Assert.Contains("Water supply", html); Assert.Contains("Local keyword", html);
        Assert.Contains("ಶಾಲೆಯಲ್ಲಿ", WebUtility.HtmlDecode(html));
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Empty(await db.Grievances.ToListAsync());
        var receipt = Hidden(html, "PreviewToken"); Assert.NotEmpty(receipt);
        var fields = new Dictionary<string, string> { ["PreviewToken"] = receipt, ["Permission"] = "true" };
        var first = await Post(client, "/Intake/Import?handler=Import", fields);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Contains("3 new", await first.Content.ReadAsStringAsync());
        var retry = await Post(client, "/Intake/Import?handler=Import", fields);
        Assert.Contains("3 already saved", await retry.Content.ReadAsStringAsync());
        var messages = await db.InboxMessages.ToListAsync(); Assert.Equal(3, messages.Count);
        Assert.All(messages, item => Assert.Equal("Pending", item.State));
        Assert.Equal(Water, messages.Single(m => m.Channel == "Email").OriginalText);
        Assert.Empty(await db.Grievances.ToListAsync());
    }

    [Theory]
    [InlineData("channel,external_id,language,source_url,message\nEmail,,English,,No water supply in Ward 12 for three days.", "source message ID")]
    [InlineData("channel,external_id,language,source_url,message\nSocial media,p1,English,http://bad.example.test/p1,No water supply in Ward 12 for three days.", "HTTPS")]
    [InlineData("channel,external_id,language,source_url,message\nEmail,e1,English,,\"Unclosed complaint text", "CSV")]
    [InlineData("channel,external_id,language,source_url,message\nText,t1,English,,No water supply in Ward 12 for three days.", "channel")]
    public async Task InvalidRowsCannotReachConfirmation(string csv, string error)
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var html = await Preview(client, csv); Assert.Contains(error, html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Hidden(html, "PreviewToken"));
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ImportRequiresPreviewPermissionAuthenticationAndAntiforgery()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Intake/Import")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Intake/Import?handler=Sample")).StatusCode);
        await Setup(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Intake/Import?handler=Import", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var token = Hidden(await Preview(client, Csv), "PreviewToken");
        var missingPermission = await Post(client, "/Intake/Import?handler=Import", new() { ["PreviewToken"] = token });
        Assert.Contains("Confirm permission", await missingPermission.Content.ReadAsStringAsync());
        var tampered = await Post(client, "/Intake/Import?handler=Import", new() { ["PreviewToken"] = token + "tampered", ["Permission"] = "true" });
        Assert.Contains("Preview", await tampered.Content.ReadAsStringAsync());
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ConflictAfterPreviewRollsBackEntireBatch()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var token = Hidden(await Preview(client, Csv), "PreviewToken");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
            await scope.ServiceProvider.GetRequiredService<InboxService>().CaptureAsync(owner.Id, "Reviewer", "A different field report about an issue that remains unresolved.", "Field meeting", "English", null, "field-1", true);
        }
        var response = await Post(client, "/Intake/Import?handler=Import", new() { ["PreviewToken"] = token, ["Permission"] = "true" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("different wording", await response.Content.ReadAsStringAsync());
        using var check = app.Services.CreateScope(); var items = await check.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync();
        Assert.Single(items); Assert.Equal("field-1", items[0].ExternalId);
    }

    [Fact]
    public async Task ReceiptCannotBeUsedByAnotherOfficerAndPostedCsvCannotReplacePreview()
    {
        using var first = new TestApp(); using var client = first.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var token = Hidden(await Preview(client, Csv), "PreviewToken");
        using var second = new TestApp(); using var other = second.CreateClient(new() { AllowAutoRedirect = false }); await Setup(other);
        var rejected = await Post(other, "/Intake/Import?handler=Import", new() { ["PreviewToken"] = token, ["Permission"] = "true" });
        Assert.Contains("Preview", await rejected.Content.ReadAsStringAsync());
        using (var scope = second.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
        var result = await Post(client, "/Intake/Import?handler=Import", new() { ["PreviewToken"] = token, ["Permission"] = "true", ["Input.Csv"] = "replaced" });
        Assert.Contains("3 new", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LargeKannadaFormCanBePreviewedAndConfirmedOverHttp()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var original = new string('ನ', 6000);
        var csv = Header + string.Join("\n", Enumerable.Range(1,5).Select(i => $"Email,unicode-{i},Kannada,,{original}"));
        using (var body = new FormUrlEncodedContent(new Dictionary<string,string> { ["Input.Csv"] = csv }))
            Assert.True((await body.ReadAsByteArrayAsync()).Length > 256 * 1024);
        var token = Hidden(await Preview(client, csv), "PreviewToken"); Assert.NotEmpty(token);
        var imported = await Post(client, "/Intake/Import?handler=Import", new() { ["PreviewToken"] = token, ["Permission"] = "true" });
        Assert.Contains("5 new", await imported.Content.ReadAsStringAsync());
        using var scope = app.Services.CreateScope(); var items = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync();
        Assert.Equal(5, items.Count); Assert.All(items, item => Assert.Equal(original, item.OriginalText));
    }

    [Fact]
    public async Task EditingPreviewRestoresOriginalBatchAndDoesNotSave()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var token = Hidden(await Preview(client, Csv), "PreviewToken");
        var response = await Post(client, "/Intake/Import?handler=Edit", new() { ["PreviewToken"] = token });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(Water, html); Assert.Contains("Preview messages", html); Assert.Empty(Hidden(html, "PreviewToken"));
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task OriginalHtmlLikeTextIsEncodedInPreview()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var html = await Preview(client, Header + "Email,e1,English,,<script>alert('complaint')</script> No water supply for three days.");
        Assert.DoesNotContain("<script>alert('complaint')</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task DemoMessagesRemainSamplesWhenPreparedAsCases()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var sample = await client.GetStringAsync("/Intake/Import?handler=Sample");
        var token = Hidden(await Preview(client, sample), "PreviewToken");
        Assert.NotEmpty(token);
        await Post(client, "/Intake/Import?handler=Import", new() { ["PreviewToken"] = token, ["Permission"] = "true" });
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var source = await db.InboxMessages.SingleAsync(m => m.Channel == "Email"); var owner = await db.Operators.SingleAsync();
        var item = await scope.ServiceProvider.GetRequiredService<CaseService>().CreateFromInboxAsync(source.Id, source.Version, owner.Id, owner.Name, source.OriginalText, source.Language, "Mysuru", "Ward 12", true);
        Assert.True(item.IsSample); Assert.Equal("Email", item.Source); Assert.Equal("Draft", item.Status);
        Assert.Contains(item.History, h => h.Action.Contains("fictional", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> Preview(HttpClient client, string csv)
    {
        var result = await Post(client, "/Intake/Import?handler=Preview", new() { ["Input.Csv"] = csv });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode); return await result.Content.ReadAsStringAsync();
    }
    private static string Hidden(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"" + name + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static async Task Setup(HttpClient client) => Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Setup", new() { ["Input.Name"] = "Import reviewer", ["Input.Email"] = "import@example.test", ["Input.Password"] = "Import-Test-Password-72!" })).StatusCode);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string,string> fields)
    {
        var page = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        fields["__RequestVerificationToken"] = Hidden(await page.Content.ReadAsStringAsync(), "__RequestVerificationToken");
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
}
