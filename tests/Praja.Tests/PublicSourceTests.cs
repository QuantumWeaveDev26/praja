using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class PublicSourceTests
{
    private const string Secret = "synthetic-youtube-key-not-real";
    [Fact]
    public async Task IntakeRoleCannotReadOrChangePublicSourceSettings()
    {
        var handler = new Reply(); using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var csrf = Csrf(await client.GetStringAsync("/PublicSources"));
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        db.OfficerRoles.Add(new() { Id = owner.Id, Role = "Intake" }); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/PublicSources")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/PublicSources?handler=Collect", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf, ["Permission"] = "true", ["Source"] = "hindu-karnataka" }))).StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AtomFeedAndKannadaQueryPreserveLanguageAndAttribution()
    {
        var handler = new Reply { Body = "<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>ಬೆಂಗಳೂರು ನೀರು ಸಮಸ್ಯೆ</title><link rel='alternate' href='https://citizenmatters.in/test/'/><summary>ದಯವಿಟ್ಟು ನೀರು ಸರಬರಾಜು ಮಾಡಿ.</summary><published>2026-10-16T10:00:00Z</published></entry></feed>" };
        using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        await Post(client, "Save", new() { ["Query"] = "ಬೆಂಗಳೂರು ನೀರು" }); await Post(client, "Collect", new() { ["Source"] = "citizen-matters", ["Permission"] = "true" });
        using var scope = app.Services.CreateScope(); var item = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.SingleAsync();
        Assert.Equal("Kannada", item.Language); Assert.Contains("Citizen Matters", item.OriginalText); Assert.Contains("ಬೆಂಗಳೂರು ನೀರು", item.OriginalText);
    }

    [Theory]
    [InlineData("{\"items\":[{\"id\":{\"videoId\":\"Abcdef123_-\"},\"snippet\":{\"title\":\"Bengaluru water\"}},{\"id\":{\"videoId\":\"invalid\"},\"snippet\":{\"title\":\"bad\"}}]}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("oversize")]
    public async Task MalformedAndOversizedYouTubeResponsesDoNotRetainPartialResults(string body)
    {
        var handler = new Reply { Body = body == "oversize" ? new string('x', 1_000_001) : body };
        using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        await Post(client, "Save", new() { ["Query"] = "Bengaluru water", ["ApiKey"] = Secret });
        await Post(client, "Collect", new() { ["Source"] = "youtube", ["Permission"] = "true" });
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync()); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PageRequiresLoginCsrfPermissionAndCurrentSettingsWithoutLeakingKey()
    {
        var handler = new Reply(); using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/PublicSources")).StatusCode);
        await Setup(client);
        foreach (var action in new[] { "Save", "RemoveKey", "Collect" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/PublicSources?handler=" + action, new FormUrlEncodedContent([]))).StatusCode);
        await Post(client, "Save", new() { ["Query"] = "Bengaluru water", ["ApiKey"] = Secret });
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/PublicSources"));
        foreach (var file in Directory.GetFiles(app.SettingsDirectory, "public-sources-*.protected")) Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(file));
        await Post(client, "Collect", new() { ["Source"] = "youtube" });
        await Post(client, "Collect", new() { ["Source"] = "youtube", ["Permission"] = "true", ["Version"] = Guid.Empty.ToString() });
        await Post(client, "Collect", new() { ["Source"] = "http://127.0.0.1/private", ["Permission"] = "true" });
        Assert.Empty(handler.Requests);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<PublicSourceSettings>();
        Assert.Empty(store.Read(Guid.NewGuid()).YouTubeKey);
        await Post(client, "RemoveKey", new()); Assert.Empty(store.Read(owner.Id).YouTubeKey);
    }

    [Fact]
    public async Task YouTubeUsesBoundedFixedEndpointAndPreservesMetadataWithOwnerScopedDeduplication()
    {
        var handler = new Reply { Body = Video("Abcdef123_-", "Bengaluru water shortage") };
        using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        await Post(client, "Save", new() { ["Query"] = "Bengaluru water", ["ApiKey"] = Secret });
        await Post(client, "Collect", new() { ["Source"] = "youtube", ["Permission"] = "true" });
        var request = Assert.Single(handler.Requests); Assert.Equal("www.googleapis.com", request.Url.Host); Assert.Equal(Secret, request.Key);
        Assert.DoesNotContain(Secret, request.Url.ToString()); var args = QueryHelpers.ParseQuery(request.Url.Query);
        Assert.Equal("10", args["maxResults"].ToString()); Assert.Equal("video", args["type"].ToString()); Assert.Equal("Bengaluru water", args["q"].ToString()); Assert.True(args.ContainsKey("publishedAfter"));
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        var item = await db.InboxMessages.SingleAsync(); Assert.Equal("Pending", item.State); Assert.Equal("youtube:Abcdef123_-", item.ExternalId);
        Assert.Contains("Bengaluru water shortage", item.OriginalText); Assert.Equal("https://www.youtube.com/watch?v=Abcdef123_-", item.SourceUrl);
        var store = scope.ServiceProvider.GetRequiredService<PublicSourceSettings>(); var service = scope.ServiceProvider.GetRequiredService<PublicSourceCollection>();
        handler.Body = Video("Abcdef123_-", "Edited water report");
        Assert.Equal(1, (await service.RunAsync(owner.Id, "youtube", store.Read(owner.Id).Version)).Existing);
        Assert.Contains("Bengaluru water shortage", (await db.InboxMessages.SingleAsync()).OriginalText);
        Assert.Empty(await db.Grievances.ToListAsync());
        var foreign = new Operator { Name = "Other owner", BootstrapSlot = 2, Email = "other-public@example.test", PasswordHash = "unused" };
        db.Operators.Add(foreign); await db.SaveChangesAsync(); store.Save(foreign.Id, Secret, "water");
        Assert.Equal(1, (await service.RunAsync(foreign.Id, "youtube", store.Read(foreign.Id).Version)).Retained);
    }

    [Fact]
    public async Task RssWorksWithoutKeyFiltersWordsRejectsUnsafeLinksAndLimitsMatches()
    {
        var items = Enumerable.Range(0, 15).Select(i => $"<item><title>Bengaluru water shortage {i}</title><link>https://www.thehindu.com/news/{i}</link><description><![CDATA[<p>Residents request water &amp; repairs.</p>]]></description><pubDate>Fri, 16 Oct 2026 10:00:00 GMT</pubDate></item>");
        var handler = new Reply { Body = "<rss><channel>" + string.Join("", items) + "<item><title>Bengaluru water</title><link>https://127.0.0.1/private</link></item><item><title>Delhi water</title><link>https://www.thehindu.com/news/delhi</link></item></channel></rss>" };
        using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        var store = scope.ServiceProvider.GetRequiredService<PublicSourceSettings>(); var service = scope.ServiceProvider.GetRequiredService<PublicSourceCollection>();
        var result = await service.RunAsync(owner.Id, "hindu-karnataka", store.Read(owner.Id).Version);
        Assert.Equal(10, result.Retained); Assert.Equal(16, result.Examined); Assert.Equal(1, result.Skipped);
        Assert.Equal(PublicSourceCollection.Feeds[0].Url, Assert.Single(handler.Requests).Url.AbsoluteUri); Assert.Empty(handler.Requests[0].Key);
        var captured = await db.InboxMessages.ToListAsync(); Assert.All(captured, i => { Assert.Equal("News/RSS", i.Channel); Assert.DoesNotContain("<p>", i.OriginalText); Assert.Contains("water & repairs", i.OriginalText); });
        Assert.Equal(10, (await service.RunAsync(owner.Id, "hindu-karnataka", store.Read(owner.Id).Version)).Existing);
        Assert.Equal(10, await db.InboxMessages.CountAsync()); Assert.Empty(await db.Grievances.ToListAsync());
    }

    [Theory]
    [InlineData("<html>Not a feed</html>")]
    [InlineData("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///C:/secret'>]><rss><channel><item><title>&x;</title></item></channel></rss>")]
    [InlineData("<rss><channel><item><title>Bengaluru water</title><link>https://www.thehindu.com/a</link></item><broken></channel></rss>")]
    public async Task InvalidFeedNeverImportsPartialBatchOrExternalEntities(string body)
    {
        var handler = new Reply { Body = body }; using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        await Post(client, "Collect", new() { ["Source"] = "hindu-karnataka", ["Permission"] = "true" });
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync()); Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(403)] [InlineData(429)] [InlineData(302)] [InlineData(500)]
    public async Task ErrorsNeverLeakProviderBodyOrRetry(int status)
    {
        var handler = new Reply { Body = Secret, Status = (HttpStatusCode)status }; using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        await Post(client, "Save", new() { ["Query"] = "Bengaluru water", ["ApiKey"] = Secret });
        var response = await Post(client, "Collect", new() { ["Source"] = "youtube", ["Permission"] = "true" });
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync()); Assert.Single(handler.Requests);
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("youtube")]
    [InlineData("hindu-karnataka")]
    public async Task ReviewCreatesLocalDraftAndAutomationDoesNotSendPublicReportsToAi(string source)
    {
        var handler = new Reply { Body = source == "youtube" ? Video("Abcdef123_-", "Bengaluru water shortage") : "<rss><channel><item><title>Bengaluru water shortage</title><link>https://www.thehindu.com/news/test</link><description>No water for three days. Please restore supply.</description></item></channel></rss>" };
        using var app = new TestApp(handler); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        await Post(client, "Save", new() { ["Query"] = "Bengaluru water", ["ApiKey"] = Secret });
        await Post(client, "Collect", new() { ["Source"] = source, ["Permission"] = "true" });
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync(); var item = await db.InboxMessages.SingleAsync();
        var automation = scope.ServiceProvider.GetRequiredService<AutomationStore>(); automation.Update(owner.Id, s => s with { Enabled = true });
        await scope.ServiceProvider.GetRequiredService<AutomationService>().RunAsync(owner.Id); Assert.Empty(automation.Read(owner.Id).Prepared);
        automation.Update(owner.Id, s => s with { Enabled = false });
        var path = "/Grievances/New?InboxId=" + item.Id;
        var reviewHtml = await client.GetStringAsync(path);
        Assert.Contains("I am authorised to retain and review this public report", reviewHtml);
        Assert.DoesNotContain("I confirm that the citizen agreed", reviewHtml);
        Assert.DoesNotContain("Need more details? Ask the citizen", reviewHtml);
        Assert.True(reviewHtml.IndexOf("This is a public report for review", StringComparison.Ordinal) < reviewHtml.IndexOf("class=\"intake-grid\"", StringComparison.Ordinal));
        Assert.Matches("class=\"intake-grid\">\\s*<section class=\"panel form-panel\">", reviewHtml);
        var fields = new Dictionary<string, string> { ["InboxId"] = item.Id.ToString(), ["InboxVersion"] = item.Version.ToString(), ["Input.OriginalText"] = item.OriginalText,
            ["Input.Language"] = "English", ["Input.Source"] = item.Channel, ["Input.District"] = "Bengaluru Urban", ["Input.Location"] = "Ward 12", ["Input.Consent"] = "true", ["__RequestVerificationToken"] = Csrf(await client.GetStringAsync(path)) };
        var analysis = await client.PostAsync(path + "&handler=Analyze", new FormUrlEncodedContent(fields)); Assert.Contains("Local keyword", await analysis.Content.ReadAsStringAsync());
        fields["__RequestVerificationToken"] = Csrf(await analysis.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(path, new FormUrlEncodedContent(fields))).StatusCode);
        var saved = await db.Grievances.Include(g => g.History).SingleAsync(); Assert.Equal("Draft", saved.Status); Assert.Equal(item.Channel, saved.Source);
        Assert.Contains(saved.History, h => h.Action.Contains("Citizen consent or direct submission was not recorded"));
        Assert.DoesNotContain(saved.History, h => h.Action == "Original complaint recorded with consent; draft prepared for officer review.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<CaseBriefService>().BuildAsync(saved.Id, owner.Id, saved.Version, [], true, true));
        Assert.Single(handler.Requests);
    }

    private static string Video(string id, string title) => JsonSerializer.Serialize(new { items = new[] { new { id = new { videoId = id }, snippet = new { title, description = "No water for three days. Please restore supply.", channelTitle = "Demo source", publishedAt = "2026-10-16T10:00:00Z" } } } });
    private static string Csrf(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task Setup(HttpClient client)
    {
        var html = await client.GetStringAsync("/Setup");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Name"] = "Public source admin", ["Input.Email"] = "sources@example.test", ["Input.Password"] = "Synthetic-Sources-123!", ["__RequestVerificationToken"] = Csrf(html) }))).StatusCode);
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string action, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync("/PublicSources"); fields["__RequestVerificationToken"] = Csrf(html);
        if (!fields.ContainsKey("Version")) fields["Version"] = Regex.Match(html, "name=\"Version\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        return await client.PostAsync("/PublicSources?handler=" + action, new FormUrlEncodedContent(fields));
    }
    private sealed class Reply : HttpMessageHandler
    {
        public List<(Uri Url, string Key)> Requests { get; } = [];
        public string Body { get; set; } = "{\"items\":[]}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("X-Goog-Api-Key", out var values) ? values.Single() : ""));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8) });
        }
    }
}
