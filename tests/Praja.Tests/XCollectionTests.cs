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

public class XCollectionTests
{
    private const string Secret = "synthetic-X-bearer-token-not-a-real-credential";
    private const string Query = "(Mysuru OR Bengaluru) (pothole OR sewage OR \"no water\") -is:retweet";
    private const string Complaint = "No water supply in Ward 12, Mysuru for three days. Please restore supply.";

    [Fact]
    public async Task SettingsRequireLoginAndCsrfAndGetNeverCallsProvider()
    {
        var provider = new XReply(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/XSettings")).StatusCode);
        await Setup(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/XSettings")).StatusCode);
        foreach (var handler in new[] { "Save", "Run", "Next", "Disconnect" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/XSettings?handler=" + handler,
                new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task TokenIsEncryptedScopedRetainedOnBlankSaveAndNeverEchoedOnValidationFailure()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client);
        Assert.Equal(HttpStatusCode.Redirect, (await Save(client)).StatusCode);
        using var scope = app.Services.CreateScope();
        var owner = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync();
        var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>();
        Assert.Equal(Secret, store.Read(owner.Id).BearerToken);
        Assert.False(store.Read(Guid.NewGuid()).Configured);
        var file = Directory.GetFiles(app.SettingsDirectory, "x-*.protected").Single();
        var encrypted = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain(Secret, encrypted); Assert.DoesNotContain(Query, encrypted);
        Assert.DoesNotContain(Secret, store.Read(owner.Id).ToString());
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/XSettings"));
        var version = store.Read(owner.Id).Version;
        Assert.Equal(HttpStatusCode.Redirect, (await Save(client, "")).StatusCode);
        Assert.Equal(Secret, store.Read(owner.Id).BearerToken);
        Assert.NotEqual(version, store.Read(owner.Id).Version);
        var invalid = await Post(client, "Save", new() { ["Input.BearerToken"] = Secret, ["Input.Query"] = "" });
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.DoesNotContain(Secret, await invalid.Content.ReadAsStringAsync());
        Assert.Equal(Query, store.Read(owner.Id).Query);
    }

    [Fact]
    public async Task TamperedAndCopiedCredentialFilesFailClosed()
    {
        using var first = new TestApp(); using var client = first.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var second = new TestApp(); using var other = second.CreateClient(new() { AllowAutoRedirect = false }); await Setup(other); await Save(other);
        var source = Directory.GetFiles(first.SettingsDirectory, "x-*.protected").Single();
        var destination = Directory.GetFiles(second.SettingsDirectory, "x-*.protected").Single();
        File.Copy(source, destination, true);
        using var scope = second.Services.CreateScope();
        var owner = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync();
        Assert.False(scope.ServiceProvider.GetRequiredService<XConfigurationStore>().Read(owner.Id).Configured);
        await File.WriteAllTextAsync(source, "tampered-data");
        using var firstScope = first.Services.CreateScope();
        var firstOwner = await firstScope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync();
        Assert.False(firstScope.ServiceProvider.GetRequiredService<XConfigurationStore>().Read(firstOwner.Id).Configured);
    }

    [Fact]
    public async Task RunRequiresExplicitPermissionAndCurrentConfigurationVersion()
    {
        var provider = new XReply(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>();
        var version = store.Read(owner.Id).Version.ToString();
        var denied = await Post(client, "Run", new() { ["Version"] = version });
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode); Assert.Empty(provider.Requests);
        await Save(client, "");
        var stale = await Post(client, "Run", new() { ["Version"] = version, ["Permission"] = "true" });
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode); Assert.Empty(provider.Requests);
        Assert.Empty(await db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task RecentSearchUsesFixedEndpointBearerHeaderAndBoundedExplicitPagination()
    {
        var provider = new XReply { Body = Batch("1900000000000000001", Complaint, "next-page") };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>();
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "Run", new() { ["Version"] = store.Read(owner.Id).Version.ToString(), ["Permission"] = "true" })).StatusCode);
        var first = Assert.Single(provider.Requests);
        Assert.Equal("GET", first.Method); Assert.Equal("https://api.x.com/2/tweets/search/recent", first.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("Bearer " + Secret, first.Authorization);
        var args = QueryHelpers.ParseQuery(first.Uri.Query);
        Assert.Equal(Query, args["query"].ToString()); Assert.Equal("10", args["max_results"].ToString());
        Assert.DoesNotContain(Secret, first.Uri.ToString()); Assert.False(args.ContainsKey("next_token"));
        Assert.Equal("next-page", store.Read(owner.Id).NextToken);
        Assert.NotNull(store.Read(owner.Id).LastSuccessUtc);
        var captured = await db.InboxMessages.SingleAsync();
        Assert.Equal("Social media", captured.Channel); Assert.Equal("X public search", captured.CapturedBy);
        Assert.Equal("Pending", captured.State); Assert.Equal(Complaint, captured.OriginalText);
        Assert.StartsWith("https://x.com/", captured.SourceUrl); Assert.Contains("1900000000000000001", captured.SourceUrl);
        Assert.Empty(await db.Grievances.ToListAsync());
        await client.GetStringAsync("/XSettings"); Assert.Single(provider.Requests);
        provider.Body = Batch("1900000000000000002", "Potholes on Bengaluru road need urgent repair; please help residents.");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "Next", new() { ["Version"] = store.Read(owner.Id).Version.ToString(), ["Permission"] = "true" })).StatusCode);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal("next-page", QueryHelpers.ParseQuery(provider.Requests[1].Uri.Query)["next_token"].ToString());
        Assert.Equal(2, await db.InboxMessages.CountAsync()); Assert.Equal("", store.Read(owner.Id).NextToken);
    }

    [Fact]
    public async Task RepeatedAndEditedPostIdsDoNotCreateDuplicateCasesOrReplaceOriginal()
    {
        var provider = new XReply { Body = Batch("1900000000000000001", Complaint) };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var service = scope.ServiceProvider.GetRequiredService<XCollectionService>();
        Assert.Equal(1, (await service.RunAsync(owner.Id)).Retained);
        Assert.Equal(1, (await service.RunAsync(owner.Id)).Existing);
        provider.Body = Batch("1900000000000000002", "Edited complaint: no water supply in Mysuru Ward 12 for four days.", null,
            ["1900000000000000001", "1900000000000000002"]);
        Assert.Equal(1, (await service.RunAsync(owner.Id)).Existing);
        var item = await db.InboxMessages.SingleAsync(); Assert.Equal(Complaint, item.OriginalText);
        Assert.Empty(await db.Grievances.ToListAsync());
    }

    [Fact]
    public async Task UnsupportedShortPostsAreSkippedAndRetainedPostsStayOwnerScoped()
    {
        var provider = new XReply { Body = Batch("1900000000000000001", "Too short") };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var service = scope.ServiceProvider.GetRequiredService<XCollectionService>();
        var result = await service.RunAsync(owner.Id); Assert.Equal(0, result.Retained); Assert.Equal(1, result.Skipped);
        Assert.Empty(await db.InboxMessages.ToListAsync());
        provider.Body = Batch("1900000000000000001", Complaint);
        await service.RunAsync(owner.Id);
        var foreign = Guid.NewGuid();
        db.Operators.Add(new() { Id = foreign, BootstrapSlot = 2, Name = "Other synthetic owner", Email = "other-x@example.test", PasswordHash = "unused-in-test" });
        await db.SaveChangesAsync();
        var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>(); store.Save(foreign, Secret, Query);
        Assert.Equal(1, (await service.RunAsync(foreign)).Retained);
        Assert.Equal(2, await db.InboxMessages.CountAsync());
        Assert.Single(await db.InboxMessages.Where(m => m.OwnerId == owner.Id).ToListAsync());
        Assert.Single(await db.InboxMessages.Where(m => m.OwnerId == foreign).ToListAsync());
    }

    [Fact]
    public async Task DisconnectClearsCredentialAndPaginationButKeepsRetainedMessages()
    {
        var provider = new XReply { Body = Batch("1900000000000000001", Complaint, "page-two") };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>();
        await scope.ServiceProvider.GetRequiredService<XCollectionService>().RunAsync(owner.Id);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "Disconnect", new())).StatusCode);
        Assert.False(store.Read(owner.Id).Configured); Assert.Empty(store.Read(owner.Id).NextToken);
        Assert.Single(await db.InboxMessages.ToListAsync()); Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ProviderErrorsDoNotRetryLeakBodiesOrAdvanceCursor(int code)
    {
        var provider = new XReply { Body = Batch("1900000000000000001", Complaint, "safe-cursor") };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var service = scope.ServiceProvider.GetRequiredService<XCollectionService>();
        var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>();
        await service.RunAsync(owner.Id); var before = store.Read(owner.Id);
        provider.Status = (HttpStatusCode)code; provider.Body = "provider secret echo " + Secret;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(owner.Id, true));
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Null(error.InnerException);
        Assert.Equal(2, provider.Requests.Count); Assert.Single(await db.InboxMessages.ToListAsync());
        var after = store.Read(owner.Id);
        Assert.Equal("safe-cursor", after.NextToken); Assert.Equal(before.LastSuccessUtc, after.LastSuccessUtc);
        Assert.Equal(before.Version, after.Version); Assert.DoesNotContain(Secret, after.Status);
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/XSettings"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"data\":[],\"meta\":{\"result_count\":1,\"next_token\":\"unsafe-cursor\"}}")]
    [InlineData("{\"errors\":[{\"detail\":\"partial results\"}],\"meta\":{\"result_count\":0}}")]
    [InlineData("{\"data\":[{\"id\":\"123\",\"text\":\"No water supply in Mysuru Ward 12 for days.\"},{\"id\":\"not-numeric\",\"text\":\"Another invalid post should invalidate entire batch.\"}],\"meta\":{\"result_count\":2}}")]
    [InlineData("{\"data\":[{\"id\":\"123\",\"text\":\"No water supply in Mysuru Ward 12 for days.\",\"edit_history_tweet_ids\":[\"456\"]}],\"meta\":{\"result_count\":1}}")]
    public async Task InvalidOrPartialResponsesNeverRetainPartialBatch(string body)
    {
        var provider = new XReply { Body = body };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<XCollectionService>().RunAsync(owner.Id));
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Empty(store.Read(owner.Id).NextToken);
        Assert.Null(store.Read(owner.Id).LastSuccessUtc); Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task MissingNextPageAndConcurrentRunDoNotSpendAndCompletedRunRejectsReplay()
    {
        var provider = new XReply(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<XConfigurationStore>();
        var service = scope.ServiceProvider.GetRequiredService<XCollectionService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(owner.Id, true));
        var gate = store.RunLock(owner.Id); await gate.WaitAsync();
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(owner.Id)); }
        finally { gate.Release(); }
        Assert.Empty(provider.Requests);
        var version = store.Read(owner.Id).Version;
        var result = await service.RunAsync(owner.Id, expectedVersion: version);
        Assert.Equal(0, result.Received); Assert.False(result.HasNextPage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(owner.Id, expectedVersion: version));
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ImportedPostAnalysisDraftAndAutomationNeverSendContentToAiProvider()
    {
        var provider = new XReply { Body = Batch("1900000000000000001", Complaint) };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        Guid inboxId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
            await scope.ServiceProvider.GetRequiredService<XCollectionService>().RunAsync(owner.Id);
            inboxId = (await db.InboxMessages.SingleAsync()).Id;
            var automation = scope.ServiceProvider.GetRequiredService<AutomationStore>();
            automation.Update(owner.Id, state => state with { Enabled = true });
            await scope.ServiceProvider.GetRequiredService<AutomationService>().RunAsync(owner.Id);
            Assert.Empty(automation.Read(owner.Id).Prepared); Assert.Single(provider.Requests);
            automation.Update(owner.Id, state => state with { Enabled = false });
        }
        var path = "/Grievances/New?InboxId=" + inboxId;
        var fields = new Dictionary<string, string>
        {
            ["InboxId"] = inboxId.ToString(), ["InboxVersion"] = "1", ["Input.OriginalText"] = Complaint,
            ["Input.Language"] = "English", ["Input.Source"] = "Social media", ["Input.District"] = "Mysuru",
            ["Input.Location"] = "Ward 12", ["Input.Consent"] = "true"
        };
        fields["__RequestVerificationToken"] = Csrf(await client.GetStringAsync(path));
        var analysis = await client.PostAsync(path + "&handler=Analyze", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, analysis.StatusCode); Assert.Single(provider.Requests);
        var analysisHtml = await analysis.Content.ReadAsStringAsync(); Assert.Contains("Local keyword", analysisHtml);
        fields["__RequestVerificationToken"] = Csrf(analysisHtml);
        var draft = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, draft.StatusCode); Assert.Single(provider.Requests);
        using var check = app.Services.CreateScope(); var saved = await check.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync();
        Assert.Equal("Draft", saved.Status); Assert.Equal(Complaint, saved.OriginalText); Assert.Equal("Social media", saved.Source);
        var briefs = check.ServiceProvider.GetRequiredService<CaseBriefService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => briefs.BuildAsync(saved.Id, saved.OwnerId, saved.Version, [], true, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => briefs.BuildAsync(saved.Id, saved.OwnerId, saved.Version, [Guid.NewGuid()], false, true));
        var brief = await briefs.BuildAsync(saved.Id, saved.OwnerId, saved.Version, [], false, false);
        Assert.Contains(brief.Brief.Points, point => point.SourceId == "original" && point.Quote.Contains("No water supply"));
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task OversizedProviderBodyIsRejectedWithoutRetention()
    {
        var provider = new XReply { Body = new string('x', 200_001) };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<XCollectionService>().RunAsync(owner.Id));
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Single(provider.Requests);
        Assert.Null(scope.ServiceProvider.GetRequiredService<XConfigurationStore>().Read(owner.Id).LastSuccessUtc);
    }

    private static string Batch(string id, string text, string? next = null, string[]? history = null)
    {
        var meta = new Dictionary<string, object> { ["result_count"] = 1 };
        if (next is not null) meta["next_token"] = next;
        return JsonSerializer.Serialize(new
        {
            data = new[] { new { id, text, lang = "en", created_at = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), edit_history_tweet_ids = history ?? [id] } },
            meta
        });
    }
    private static Task<HttpResponseMessage> Save(HttpClient client, string token = Secret) => Post(client, "Save", new() { ["Input.BearerToken"] = token, ["Input.Query"] = Query });
    private static async Task Setup(HttpClient client)
    {
        var html = await client.GetStringAsync("/Setup");
        var response = await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Name"] = "X test admin", ["Input.Email"] = "x-admin@example.test", ["Input.Password"] = "Synthetic-X-password-123!",
            ["__RequestVerificationToken"] = Csrf(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string handler, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync("/XSettings");
        fields["__RequestVerificationToken"] = Csrf(html);
        if (!fields.ContainsKey("Version")) fields["Version"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"Version\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        return await client.PostAsync("/XSettings?handler=" + handler, new FormUrlEncodedContent(fields));
    }
    private static string Csrf(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private sealed record Request(string Method, Uri Uri, string Authorization);
    private sealed class XReply : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public string Body { get; set; } = "{\"meta\":{\"result_count\":0}}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString() ?? ""));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}
