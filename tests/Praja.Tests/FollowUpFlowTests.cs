using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Xunit;

namespace Praja.Tests;

public class FollowUpFlowTests
{
    private const string Original = "No water supply in Ward 12. The citizen is asking for help with the problem.";
    [Fact]
    public async Task ConfirmedFollowUpAppearsSeparatelyInDraftWithoutChangingOriginal()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); fields["Input.FollowUpTimeline"] = "Since Monday"; fields["Input.FollowUpHelp"] = "Please restore supply and provide a tanker until then."; fields["Input.FollowUpConfirmed"] = "true";
        var response = await Post(client, "/Grievances/New", fields); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = app.Services.CreateScope(); var saved = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.Include(g => g.History).SingleAsync();
        Assert.Equal(Original, saved.OriginalText); Assert.Contains("Citizen follow-up", saved.DraftText); Assert.Contains("Since Monday", saved.DraftText); Assert.Contains("provide a tanker", saved.DraftText); Assert.Equal("Draft", saved.Status);
        Assert.DoesNotContain("Monday", saved.Summary); Assert.Contains(saved.History, h => h.Action.Contains("follow-up", StringComparison.OrdinalIgnoreCase));
    }
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 121)]
    public async Task UnconfirmedOrOverlongAnswersCannotCreateACase(bool confirmed, int timelineLength)
    {
        using var provider = new DraftProvider(); using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); fields["Input.FollowUpTimeline"] = new string('x', timelineLength); fields["Input.FollowUpConfirmed"] = confirmed.ToString();
        var response = await Post(client, "/Grievances/New", fields); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("FollowUpTimeline", await response.Content.ReadAsStringAsync());
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
        Assert.Empty(provider.Requests);
    }
    [Fact]
    public async Task InboxFollowUpPreservesSourceAndItsAtomicConversion()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var captured = await Post(client, "/Intake/New", new() { ["Input.OriginalText"] = Original, ["Input.Channel"] = "Text", ["Input.Language"] = "English", ["Input.Permission"] = "true" });
        var inboxId = captured.Headers.Location!.ToString().Split('/').Last(); var fields = Fields(); fields["InboxId"] = inboxId; fields["InboxVersion"] = "1"; fields["Input.FollowUpHelp"] = "Restore water supply."; fields["Input.FollowUpConfirmed"] = "true";
        var response = await Post(client, "/Grievances/New?InboxId=" + inboxId, fields); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var saved = await db.Grievances.SingleAsync(); var source = await db.InboxMessages.SingleAsync();
        Assert.Equal(Original, source.OriginalText); Assert.Equal(Original, saved.OriginalText); Assert.Contains("Restore water supply.", saved.DraftText); Assert.Equal(saved.Id, source.GrievanceId); Assert.Equal("Converted", source.State);
    }
    [Fact]
    public async Task ConfirmedAnswersAreNotIncludedInProviderRequest()
    {
        using var provider = new DraftProvider(); using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); fields["Input.FollowUpHelp"] = "Provide a temporary tanker at the shelter."; fields["Input.FollowUpConfirmed"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Grievances/New", fields)).StatusCode);
        Assert.Single(provider.Requests); Assert.DoesNotContain("temporary tanker", provider.Requests[0]); Assert.DoesNotContain("FollowUp", provider.Requests[0]);
        using var scope = app.Services.CreateScope(); var saved = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync();
        Assert.Contains("temporary tanker", saved.DraftText); Assert.Equal(Original, saved.OriginalText);
    }
    [Fact]
    public async Task KannadaAnswersRemainVerbatimWithKannadaSectionLabels()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); fields["Input.Language"] = "Kannada"; fields["Input.FollowUpTimeline"] = "ಮೂರು ದಿನಗಳಿಂದ"; fields["Input.FollowUpHelp"] = "ನೀರು ಸರಬರಾಜು ಪುನರಾರಂಭಿಸಿ"; fields["Input.FollowUpConfirmed"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Grievances/New", fields)).StatusCode);
        using var scope = app.Services.CreateScope(); var saved = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync();
        Assert.Contains("ಸಮಸ್ಯೆಯ ಸಮಯ: ಮೂರು ದಿನಗಳಿಂದ", saved.DraftText); Assert.Contains("ಕೋರಿರುವ ಸಹಾಯ: ನೀರು ಸರಬರಾಜು ಪುನರಾರಂಭಿಸಿ", saved.DraftText); Assert.Equal(Original, saved.OriginalText);
    }
    [Fact]
    public async Task FailedInboxFollowUpLeavesIncomingMessagePending()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var captured = await Post(client, "/Intake/New", new() { ["Input.OriginalText"] = Original, ["Input.Channel"] = "Text", ["Input.Language"] = "English", ["Input.Permission"] = "true" });
        var inboxId = captured.Headers.Location!.ToString().Split('/').Last(); var fields = Fields(); fields["InboxId"] = inboxId; fields["InboxVersion"] = "1"; fields["Input.FollowUpHelp"] = "Restore water.";
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Grievances/New?InboxId=" + inboxId, fields)).StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); Assert.Empty(await db.Grievances.ToListAsync()); var source = await db.InboxMessages.SingleAsync(); Assert.Equal("Pending", source.State); Assert.Null(source.GrievanceId); Assert.Equal(1, source.Version);
    }
    private static Dictionary<string, string> Fields() => new() { ["Input.OriginalText"] = Original, ["Input.Language"] = "English", ["Input.Source"] = "Text", ["Input.District"] = "Mysuru", ["Input.Location"] = "Ward 12", ["Input.Consent"] = "true" };
    private static async Task Setup(HttpClient client) => Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Setup", new() { ["Input.Name"] = "Follow-up reviewer", ["Input.Email"] = "followup@example.test", ["Input.Password"] = "Followup-Test-Password-72!" })).StatusCode);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(path); fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    private sealed class DraftProvider : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = JsonSerializer.Serialize(new { title = "Water supply concern", draft = Original, summary = "A water supply concern." });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } })) };
        }
    }
}
