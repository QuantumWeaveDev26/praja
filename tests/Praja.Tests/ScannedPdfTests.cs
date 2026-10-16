using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Praja.Tests;

// Synthetic images and provider replies verify transport and workflow, not OCR accuracy.
public class ScannedPdfTests
{
    private const string Complaint = "Water supply has stopped in Ward 12, Mysuru, for five days. Please restore supply.";
    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextCannotMaskBlankPageOrImageOnSamePage(bool samePageImage)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(Complaint, 10, new PdfPoint(30, 700), font);
        if (samePageImage) page.AddPng(Pixel, new PdfRectangle(30, 30, 300, 300));
        else builder.AddPage(PageSize.A4);
        var bytes = builder.Build();
        var inspection = await PdfIntakeWorker.InspectAsync(bytes, default);
        Assert.True(inspection.RequiresOcr);
        Assert.Equal(samePageImage ? 1 : 2, inspection.PageCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfIntakeWorker.ExtractAsync(bytes, default));
    }

    [Fact]
    public async Task WorkerRejectsOversizedInputAndHonoursCancellation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfIntakeWorker.InspectAsync(new byte[8 * 1024 * 1024 + 1], default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfIntakeWorker.InspectAsync(Pdf(false), cancelled.Token));
    }

    [Theory]
    [InlineData("{\"pages\":null}")]
    [InlineData("{\"pages\":[{\"page\":\"one\",\"text\":\"Water supply has stopped for five days.\"}]}")]
    [InlineData("{\"pages\":[{\"page\":1,\"text\":null}]}")]
    [InlineData("{\"pages\":[{\"page\":1,\"text\":\"Water supply has stopped\\u0001 in Ward 12 for five days.\"}]}")]
    public async Task InvalidProviderPageSchemaOrControlTextIsRejected(string content)
    {
        var handler = new PdfReply { ContentOverride = content };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).ExtractAsync(File(Pdf(true)), "English", true));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TextOnlyPdfStaysLocalEvenWhenProviderIsConfigured()
    {
        var handler = new PdfReply();
        var result = await Service(handler).ExtractAsync(File(Pdf(false)), "English", true);
        Assert.Contains(Complaint, result.Text);
        Assert.Contains("Local", result.Method);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageAndMixedPdfRequireEveryPageAndPreservePageOrder(bool mixed)
    {
        var bytes = Pdf(true, mixed ? 2 : 1, mixed);
        var inspection = await PdfIntakeWorker.InspectAsync(bytes, default);
        Assert.True(inspection.RequiresOcr);
        Assert.Equal(mixed ? 2 : 1, inspection.PageCount);
        var handler = new PdfReply { Pages = mixed ? [new(1, Complaint), new(2, "Second page: the public tap remains dry. [unclear]")] : [new(1, Complaint)] };
        var result = await Service(handler).ExtractAsync(File(bytes), "English", true);
        Assert.Contains("[Page 1]", result.Text);
        Assert.Contains(Complaint, result.Text);
        if (mixed) { Assert.Contains("[Page 2]", result.Text); Assert.Contains("[unclear]", result.Text); }
        Assert.Equal(1, handler.Calls);
        using var sent = JsonDocument.Parse(handler.LastRequest!);
        Assert.Equal("json_object", sent.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var parts = sent.RootElement.GetProperty("messages")[1].GetProperty("content");
        var pdf = parts.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "file").GetProperty("file");
        Assert.Equal("petition.pdf", pdf.GetProperty("filename").GetString());
        Assert.Equal("data:application/pdf;base64," + Convert.ToBase64String(bytes), pdf.GetProperty("file_data").GetString());
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("configuration")]
    [InlineData("malformed")]
    [InlineData("eleven-pages")]
    public async Task InvalidOrUnauthorizedPdfNeverReachesProvider(string condition)
    {
        var handler = new PdfReply();
        var service = Service(condition == "configuration" ? null : handler);
        var bytes = condition == "malformed" ? "%PDF-broken"u8.ToArray() : Pdf(true, condition == "eleven-pages" ? 11 : 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractAsync(File(bytes), "English", condition != "consent"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("missing-page")]
    [InlineData("duplicate-page")]
    [InlineData("wrong-order")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("provider-error")]
    [InlineData("invalid-json")]
    public async Task IncompleteOrFailedOcrNeverReturnsPartialText(string condition)
    {
        var handler = new PdfReply { Pages = [new(1, Complaint), new(2, Complaint)] };
        if (condition == "missing-page") handler.Pages = [new(1, Complaint)];
        if (condition == "duplicate-page") handler.Pages = [new(1, Complaint), new(1, Complaint)];
        if (condition == "wrong-order") handler.Pages = [new(2, Complaint), new(1, Complaint)];
        if (condition == "truncated") handler.FinishReason = "length";
        if (condition == "oversized") handler.Pages = [new(1, new string('x', 6001)), new(2, Complaint)];
        if (condition == "provider-error") handler.Status = HttpStatusCode.ServiceUnavailable;
        if (condition == "invalid-json") handler.InvalidJson = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).ExtractAsync(File(Pdf(true, 2, true)), "English", true));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GmailLocalPdfExtractionRejectsMixedDocumentRatherThanDroppingScan()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfIntakeWorker.ExtractAsync(Pdf(true, 2, true), default));
    }

    [Fact]
    public async Task ScannedUploadBecomesEditableTextThenReviewedRegisteredCase()
    {
        var handler = new PdfReply();
        using var app = new TestApp(handler);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Setup", new() { ["Input.Name"] = "PDF reviewer", ["Input.Email"] = "pdf@example.test", ["Input.Password"] = "Pdf-Test-Password-72!" })).StatusCode);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(await Token(client, "/Grievances/New")), "__RequestVerificationToken");
        content.Add(new StringContent("English"), "Input.Language");
        content.Add(new StringContent("true"), "Input.Consent");
        content.Add(new ByteArrayContent(Pdf(true)), "Document", "scan.pdf");
        var uploaded = await client.PostAsync("/Grievances/New?handler=Document", content);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var html = await uploaded.Content.ReadAsStringAsync();
        Assert.Contains(Complaint, html);
        Assert.Contains("textarea", html);
        using (var scope = app.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
        const string reviewed = Complaint + " Citizen confirmed the outage continues today.";
        var draft = await Post(client, "/Grievances/New", new() { ["Input.OriginalText"] = reviewed, ["Input.District"] = "Mysuru", ["Input.Location"] = "Ward 12", ["Input.Language"] = "English", ["Input.Source"] = "Document extraction", ["Input.Consent"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, draft.StatusCode);
        var path = draft.Headers.Location!.ToString();
        using (var scope = app.Services.CreateScope()) Assert.Equal("Draft", (await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync()).Status);
        var registered = await Post(client, path + "?handler=Register", new() { ["Version"] = "1", ["Title"] = "Water supply interruption", ["DraftText"] = reviewed, ["Department"] = "water", ["CategoryCode"] = "", ["SubcategoryCode"] = "", ["Reviewed"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        using var finalScope = app.Services.CreateScope();
        var db = finalScope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var saved = await db.Grievances.SingleAsync();
        Assert.Equal("Registered", saved.Status);
        Assert.Equal(reviewed, saved.OriginalText);
        Assert.Empty(await db.EvidenceAttachments.ToListAsync());
        Assert.Equal(1, handler.PdfCalls);
    }

    private static byte[] Pdf(bool image, int pages = 1, bool mixed = false)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(PageSize.A4);
            if (!image || mixed && i == 0) page.AddText(Complaint, 10, new PdfPoint(30, 700), font);
            else page.AddPng(Pixel, new PdfRectangle(30, 30, 300, 300));
        }
        return builder.Build();
    }
    private static IFormFile File(byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Document", "petition.pdf");
    private static MediaIntakeService Service(PdfReply? handler = null) => new(new ConfigurationBuilder().AddInMemoryCollection(handler is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Ai:ApiKey"] = "synthetic-test-key", ["Ai:Endpoint"] = "https://provider.example.test/chat/completions", ["Ai:Model"] = "test-vision" }).Build(), new Factory(handler));
    private sealed class Factory(PdfReply? handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler is null ? throw new InvalidOperationException("Unexpected provider call") : new(handler, false);
    }
    private sealed record Page(int page, string text);
    private sealed class PdfReply : HttpMessageHandler
    {
        public Page[] Pages = [new(1, Complaint)];
        public string FinishReason = "stop";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool InvalidJson; public string? ContentOverride;
        public int Calls, PdfCalls;
        public string? LastRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (!LastRequest.Contains("file_data")) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Synthetic drafting fallback") };
            PdfCalls++;
            var body = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = FinishReason, message = new { content = ContentOverride ?? (InvalidJson ? "bad-json" : JsonSerializer.Serialize(new { pages = Pages })) } } } });
            return new(Status) { Content = new StringContent(body) };
        }
    }
    private static async Task<string> Token(HttpClient client, string path) => WebUtility.HtmlDecode(Regex.Match(await client.GetStringAsync(path), "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await Token(client, path);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
}

