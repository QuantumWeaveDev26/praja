using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public partial class GmailFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScannedGmailPdfAutomaticallyPreparesThenRegistersExactlyOnce(bool mixed)
    {
        var bytes = ScannedMailPdf(mixed ? 2 : 1, mixed);
        var provider = new GoogleReply { CustomPayload = Multipart("scan.pdf", bytes.Length, ""), AttachmentBytes = bytes, MessageLabel = "INBOX", PdfPages = mixed ? 2 : 1 };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client); await FinishConnect(client);
        await Post(client, "/Automation?handler=Enable", new() { ["Permission"] = "true" });
        await Post(client, "/Automation?handler=Run", new());
        using var scope = app.Services.CreateScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        var item = await db.InboxMessages.SingleAsync();
        Assert.Contains("[Page 1]", item.OriginalText); Assert.Contains("Ward 12", item.OriginalText);
        if (mixed) Assert.Contains("[Page 2]", item.OriginalText);
        Assert.Single(sp.GetRequiredService<AutomationStore>().Read(owner.Id).Prepared);
        Assert.Empty(await db.Grievances.ToListAsync()); Assert.Equal(1, provider.PdfCalls);
        var reads = provider.AttachmentReads;
        await Post(client, "/Automation?handler=Run", new()); Assert.Equal(reads, provider.AttachmentReads); Assert.Equal(1, provider.PdfCalls);
        var approval = new Dictionary<string, string> { ["Version"] = "1", ["Title"] = "Water supply complaint", ["DraftText"] = "No water supply in Ward 12, Mysuru. Please restore supply.", ["District"] = "Mysuru", ["Location"] = "Ward 12", ["Department"] = "water", ["CategoryCode"] = "water_supply", ["SubcategoryCode"] = "no_supply", ["Reviewed"] = "true" };
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Automation/Review/" + item.Id + "?handler=Approve", approval)).StatusCode);
        var grievance = await db.Grievances.SingleAsync(); Assert.Equal("Registered", grievance.Status); Assert.Contains("[Page 1]", grievance.OriginalText);
        Assert.Empty(await db.EvidenceAttachments.ToListAsync()); Assert.Equal(0, provider.WriteCalls);
    }

    [Theory]
    [InlineData("provider-error")]
    [InlineData("missing-page")]
    [InlineData("truncated")]
    public async Task ScannedGmailFailureRemainsUnseenAndRetriesWithoutPartialImport(string failure)
    {
        var bytes = ScannedMailPdf(2, true);
        var provider = new GoogleReply { CustomPayload = Multipart("scan.pdf", bytes.Length), AttachmentBytes = bytes, MessageLabel = "INBOX", PdfPages = 2, PdfFailure = failure };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client); await FinishConnect(client); await Post(client, "/Automation?handler=Enable", new() { ["Permission"] = "true" });
        await Post(client, "/Automation?handler=Run", new());
        using var scope = app.Services.CreateScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Empty(await db.Grievances.ToListAsync());
        var failed = sp.GetRequiredService<AutomationStore>().Read(owner.Id); Assert.Empty(failed.Seen); Assert.Empty(failed.Prepared); Assert.Empty(failed.ManualReview);
        Assert.Equal(1, provider.PdfCalls);
        provider.PdfFailure = null; await Post(client, "/Automation?handler=Run", new());
        Assert.Single(await db.InboxMessages.ToListAsync()); Assert.Single(sp.GetRequiredService<AutomationStore>().Read(owner.Id).Prepared); Assert.Equal(2, provider.PdfCalls);
        await Post(client, "/Automation?handler=Run", new()); Assert.Equal(2, provider.PdfCalls); Assert.Equal(0, provider.WriteCalls);
    }

    [Fact]
    public async Task ScannedGmailPreviewStaysLocalAndDisabledAutomationCannotSendPdf()
    {
        var bytes = ScannedMailPdf();
        var provider = new GoogleReply { CustomPayload = Multipart("scan.pdf", bytes.Length), AttachmentBytes = bytes };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client); await FinishConnect(client);
        var preview = await Post(client, "/GmailSettings?handler=Preview", new()); var html = await preview.Content.ReadAsStringAsync();
        Assert.Contains("no partial message imported", html); Assert.DoesNotContain("Confirm email import", html); Assert.Equal(0, provider.PdfCalls);
        provider.MessageLabel = "INBOX"; await Post(client, "/Automation?handler=Run", new());
        Assert.Equal(0, provider.PdfCalls);
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ScannedGmailPdfLargerThanOldTextLimitCanBeDownloadedAndProcessed()
    {
        // A trailing comment preserves a valid PDF while exercising bounded large-part transport.
        var bytes = ScannedMailPdf().Concat(Encoding.ASCII.GetBytes("\n%" + new string('x', 150_000) + "\n")).ToArray();
        var provider = new GoogleReply { CustomPayload = Multipart("scan.pdf", bytes.Length), AttachmentBytes = bytes, MessageLabel = "INBOX" };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client); await FinishConnect(client); await Post(client, "/Automation?handler=Enable", new() { ["Permission"] = "true" });
        await Post(client, "/Automation?handler=Run", new());
        using var scope = app.Services.CreateScope(); Assert.Single(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
        Assert.Equal(1, provider.AttachmentReads); Assert.Equal(1, provider.PdfCalls); Assert.Equal(0, provider.WriteCalls);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("eleven-pages")]
    [InlineData("malformed")]
    public async Task UnsupportedScannedGmailPdfNeverReachesProviderOrImportsPartialBody(string failure)
    {
        var bytes = failure == "malformed" ? "%PDF-broken"u8.ToArray() : ScannedMailPdf(failure == "eleven-pages" ? 11 : 1);
        var provider = new GoogleReply { CustomPayload = Multipart("scan.pdf", failure == "oversized" ? 8 * 1024 * 1024 + 1 : bytes.Length, "<p>No water supply in Ward 12. Please repair the water supply.</p>"), AttachmentBytes = bytes, MessageLabel = "INBOX" };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client); await FinishConnect(client); await Post(client, "/Automation?handler=Enable", new() { ["Permission"] = "true" });
        await Post(client, "/Automation?handler=Run", new());
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        Assert.Empty(await db.InboxMessages.ToListAsync()); Assert.Empty(await db.Grievances.ToListAsync()); Assert.Equal(0, provider.PdfCalls);
        Assert.Single(scope.ServiceProvider.GetRequiredService<AutomationStore>().Read(owner.Id).ManualReview);
        if (failure == "oversized") Assert.Equal(0, provider.AttachmentReads);
    }

    [Fact]
    public async Task FailedScannedEmailDoesNotBlockAnotherSupportedComplaint()
    {
        var bytes = ScannedMailPdf();
        var provider = new GoogleReply { CustomPayload = Multipart("scan.pdf", bytes.Length), AttachmentBytes = bytes, MessageLabel = "INBOX", PdfFailure = "provider-error", SecondTextMessage = true };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client); await FinishConnect(client); await Post(client, "/Automation?handler=Enable", new() { ["Permission"] = "true" });
        await Post(client, "/Automation?handler=Run", new());
        using var scope = app.Services.CreateScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        Assert.Contains("Street lights", (await db.InboxMessages.SingleAsync()).OriginalText);
        var state = sp.GetRequiredService<AutomationStore>().Read(owner.Id); Assert.Single(state.Prepared); Assert.Single(state.Seen); Assert.Empty(state.ManualReview);
        provider.PdfFailure = null; await Post(client, "/Automation?handler=Run", new());
        Assert.Equal(2, await db.InboxMessages.CountAsync()); Assert.Equal(2, sp.GetRequiredService<AutomationStore>().Read(owner.Id).Prepared.Length);
        Assert.Equal(0, provider.WriteCalls);
    }

    [Fact]
    public async Task RetrySkippedReprocessesPdfWithoutManualUploadAndPreservesOtherSeenMessages()
    {
        var provider = new GoogleReply { CustomPayload = Multipart("scan.pdf", 100), AttachmentBytes = "%PDF-broken"u8.ToArray(), MessageLabel = "INBOX", SecondTextMessage = true };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client); await FinishConnect(client); await Post(client, "/Automation?handler=Enable", new() { ["Permission"] = "true" });
        await Post(client, "/Automation?handler=Run", new());
        using var scope = app.Services.CreateScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        var store = sp.GetRequiredService<AutomationStore>(); Assert.Single(store.Read(owner.Id).ManualReview); Assert.Equal(2, store.Read(owner.Id).Seen.Length);
        provider.AttachmentBytes = ScannedMailPdf();
        await Post(client, "/Automation?handler=RetrySkipped", new());
        Assert.Empty(store.Read(owner.Id).ManualReview); Assert.Single(store.Read(owner.Id).Seen);
        await Post(client, "/Automation?handler=Run", new());
        Assert.Equal(2, await db.InboxMessages.CountAsync()); Assert.Equal(2, store.Read(owner.Id).Prepared.Length); Assert.Equal(1, provider.PdfCalls);
        await Post(client, "/Automation?handler=Run", new()); Assert.Equal(2, await db.InboxMessages.CountAsync()); Assert.Equal(1, provider.PdfCalls);
    }

    private static byte[] ScannedMailPdf(int pages = 1, bool mixed = false)
    {
        var pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (mixed && i == 0) page.AddText("No water supply in Ward 12, Mysuru. Please restore supply.", 12, new UglyToad.PdfPig.Core.PdfPoint(30, 700), font);
            else page.AddPng(pixel, new UglyToad.PdfPig.Core.PdfRectangle(30, 30, 300, 300));
        }
        return builder.Build();
    }
}
