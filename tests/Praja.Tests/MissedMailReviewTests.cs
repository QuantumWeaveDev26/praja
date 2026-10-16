using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public partial class GmailFlowTests
{
    private const string MissedMailText = "The taps in Ward 12, Mysuru, have been dry since Monday. Kindly arrange a tanker for the residents.";
    private static GoogleReply MissedMailProvider() => new()
    {
        MessageLabel = "INBOX", MessageText = MissedMailText,
        CustomPayload = new { mimeType = "text/plain", body = new { data = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(MissedMailText)) } }
    };
    private static async Task StartMissedMail(HttpClient client)
    {
        await Setup(client); await Save(client); await FinishConnect(client);
        await Post(client, "/Automation?handler=Enable", new() { ["Permission"] = "true" });
        await Post(client, "/Automation?handler=Run", new());
    }

    [Fact]
    public async Task MissedComplaintReferenceCanBeRecheckedPreparedAndRegisteredExactlyOnce()
    {
        Assert.False(AutomationService.LooksLikeComplaint(MissedMailText));
        var provider = MissedMailProvider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await StartMissedMail(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<AutomationStore>();
        var state = store.Read(owner.Id);
        Assert.Equal(new ManualMailReview(Mailbox, "abc123"), Assert.Single(state.NonComplaintReview));
        Assert.Single(state.Seen); Assert.Empty(state.Prepared); Assert.Empty(await db.InboxMessages.ToListAsync());
        Assert.DoesNotContain(MissedMailText, await client.GetStringAsync("/Automation"));
        var reads = provider.MessageReads;
        await Post(client, "/Automation?handler=Run", new()); Assert.Equal(reads, provider.MessageReads);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Automation?handler=RecheckComplaint", new() { ["MessageId"] = "abc123", ["Permission"] = "true" })).StatusCode);
        Assert.Single(store.Read(owner.Id).ForcedReview);
        await Post(client, "/Automation?handler=Run", new());
        var item = await db.InboxMessages.SingleAsync(); Assert.Equal(MissedMailText, item.OriginalText);
        Assert.Single(store.Read(owner.Id).Prepared); Assert.Empty(store.Read(owner.Id).ForcedReview);
        Assert.Empty(store.Read(owner.Id).NonComplaintReview); Assert.Empty(await db.Grievances.ToListAsync());
        reads = provider.MessageReads; await Post(client, "/Automation?handler=Run", new()); Assert.Equal(reads, provider.MessageReads);
        Assert.Single(await db.InboxMessages.ToListAsync());
        var approval = new Dictionary<string, string> { ["Version"] = "1", ["Title"] = "Dry taps in Ward 12", ["DraftText"] = MissedMailText,
            ["District"] = "Mysuru", ["Location"] = "Ward 12", ["Department"] = "water", ["CategoryCode"] = "water_supply", ["SubcategoryCode"] = "no_supply", ["Reviewed"] = "true" };
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Automation/Review/" + item.Id + "?handler=Approve", approval)).StatusCode);
        var grievance = await db.Grievances.SingleAsync(); Assert.Equal("Registered", grievance.Status); Assert.Equal(MissedMailText, grievance.OriginalText);
        await Post(client, "/Automation?handler=Run", new()); Assert.Single(await db.Grievances.ToListAsync()); Assert.Equal(0, provider.WriteCalls);
    }

    [Fact]
    public async Task MissedComplaintRecheckRequiresPermissionAndRejectsForgedReferences()
    {
        var provider = MissedMailProvider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await StartMissedMail(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<AutomationStore>();
        var reads = provider.MessageReads;
        await Post(client, "/Automation?handler=RecheckComplaint", new() { ["MessageId"] = "abc123" });
        Assert.Empty(store.Read(owner.Id).ForcedReview);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, "/Automation?handler=RecheckComplaint", new() { ["MessageId"] = "def456", ["Permission"] = "true" })).StatusCode);
        Assert.Empty(store.Read(owner.Id).ForcedReview); Assert.Equal(reads, provider.MessageReads);
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Single(store.Read(owner.Id).NonComplaintReview);
    }

    [Fact]
    public async Task MissedComplaintFailedFetchKeepsRecheckForNextRun()
    {
        var provider = MissedMailProvider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await StartMissedMail(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<AutomationStore>();
        await Post(client, "/Automation?handler=RecheckComplaint", new() { ["MessageId"] = "abc123", ["Permission"] = "true" });
        provider.FailFetch = true; await Post(client, "/Automation?handler=Run", new());
        Assert.Single(store.Read(owner.Id).ForcedReview); Assert.Single(store.Read(owner.Id).NonComplaintReview);
        Assert.Empty(await db.InboxMessages.ToListAsync());
        provider.FailFetch = false; await Post(client, "/Automation?handler=Run", new());
        Assert.Single(await db.InboxMessages.ToListAsync()); Assert.Single(store.Read(owner.Id).Prepared); Assert.Empty(store.Read(owner.Id).ForcedReview);
        Assert.Equal(0, provider.WriteCalls);
    }

    [Fact]
    public async Task DismissingMissedMailRemovesQueuedRecheckButKeepsSeenMarker()
    {
        var provider = MissedMailProvider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await StartMissedMail(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<AutomationStore>();
        var seen = store.Read(owner.Id).Seen;
        await Post(client, "/Automation?handler=RecheckComplaint", new() { ["MessageId"] = "abc123", ["Permission"] = "true" });
        Assert.Single(store.Read(owner.Id).ForcedReview);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Automation?handler=DismissNonComplaint", new() { ["MessageId"] = "abc123" })).StatusCode);
        Assert.Empty(store.Read(owner.Id).ForcedReview); Assert.Empty(store.Read(owner.Id).NonComplaintReview); Assert.Equal(seen, store.Read(owner.Id).Seen);
        var reads = provider.MessageReads; await Post(client, "/Automation?handler=Run", new()); Assert.Equal(reads, provider.MessageReads);
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Equal(0, provider.WriteCalls);
    }

    [Fact]
    public async Task RecheckedMissedMailWithUnsupportedAttachmentMovesToManualReviewWithoutPartialImport()
    {
        var provider = MissedMailProvider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await StartMissedMail(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<AutomationStore>();
        await Post(client, "/Automation?handler=RecheckComplaint", new() { ["MessageId"] = "abc123", ["Permission"] = "true" });
        provider.CustomPayload = Multipart("attachment.exe", 100, MissedMailText);
        await Post(client, "/Automation?handler=Run", new());
        Assert.Empty(store.Read(owner.Id).ForcedReview); Assert.Empty(store.Read(owner.Id).NonComplaintReview);
        Assert.Equal(new ManualMailReview(Mailbox, "abc123"), Assert.Single(store.Read(owner.Id).ManualReview));
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Empty(store.Read(owner.Id).Prepared);
        Assert.Equal(0, provider.AttachmentReads); Assert.Equal(0, provider.WriteCalls);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("different-mailbox")]
    public async Task MissedMailActionsCannotUseDisconnectedOrDifferentMailbox(string change)
    {
        var provider = MissedMailProvider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await StartMissedMail(client);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var owner = await db.Operators.SingleAsync(); var store = scope.ServiceProvider.GetRequiredService<AutomationStore>();
        if (change == "disconnected") await Post(client, "/GmailSettings?handler=Disconnect", new());
        else scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>().Update(owner.Id, c => c with { Mailbox = "other@example.test" });
        foreach (var handler in new[] { "RecheckComplaint", "DismissNonComplaint" })
            Assert.Equal(HttpStatusCode.NotFound, (await Post(client, "/Automation?handler=" + handler, new() { ["MessageId"] = "abc123", ["Permission"] = "true" })).StatusCode);
        Assert.Empty(store.Read(owner.Id).ForcedReview); Assert.Single(store.Read(owner.Id).NonComplaintReview); Assert.Empty(await db.InboxMessages.ToListAsync());
    }
}
