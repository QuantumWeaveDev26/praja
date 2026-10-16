using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Microsoft.AspNetCore.Http;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Praja.Tests;

public class CaseBriefFlowTests
{
    private const string Original = "No water supply in Ward 12, Mysuru, for three days. Please restore supply.";
    private const string Report = "The water supply pipe was repaired and checked with residents on the same day. Supply resumed in Ward 12.";

    [Fact]
    public async Task CitizenDisagreementIsAlwaysVisibleEvenIfAiOmitsIt()
    {
        var provider = new ReplyHandler("{\"points\":[{\"sourceId\":\"original\",\"quote\":\"" + Original + "\"},{\"sourceId\":\"resolution\",\"quote\":\"" + Report + "\"}]}");
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        using (var scope = app.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var item = await db.Grievances.SingleAsync(); item.CitizenFeedback = "Disagreed"; await db.SaveChangesAsync(); }
        var response = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1", ["UseAi"] = "true", ["ProcessingConsent"] = "true" });
        var html = await response.Content.ReadAsStringAsync(); Assert.Contains("AI-selected source excerpts", html);
        Assert.Contains("Citizen says the issue remains unresolved", html); Assert.Contains("#brief-source-feedback", html);
    }

    [Fact]
    public async Task SelectedPdfIsReadLocallyAndBriefKeepsItsCitationAndEvidenceUnchanged()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        Guid attachment;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
            var builder = new PdfDocumentBuilder(); var page = builder.AddPage(PageSize.A4); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            page.AddText("Water supply inspection recorded stable flow in Ward 12.", 12, new PdfPoint(30,700), font);
            var bytes = builder.Build(); using var stream = new MemoryStream(bytes);
            var file = new FormFile(stream, 0, bytes.Length, "file", "inspection.pdf");
            attachment = (await scope.ServiceProvider.GetRequiredService<EvidenceService>().UploadAsync(id, owner.Id, owner.Name, 1, file, "Recorded inspection", true)).Id;
        }
        var response = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "2", ["SelectedEvidence"] = attachment.ToString(), ["ProcessingConsent"] = "true" });
        var html = await response.Content.ReadAsStringAsync(); Assert.Contains("Local PDF", html); Assert.Contains("stable flow", html);
        Assert.Contains("#brief-source-document-" + attachment.ToString("N"), html);
        using var check = app.Services.CreateScope(); var savedDb = check.ServiceProvider.GetRequiredService<WorkspaceDb>();
        Assert.Equal(2, (await savedDb.Grievances.SingleAsync()).Version); Assert.Single(await savedDb.EvidenceAttachments.ToListAsync());
    }

    [Fact]
    public async Task SelectedFilesRequirePermissionAndLocalModeDoesNotSendImagesForOcr()
    {
        var provider = new ReplyHandler("{}"); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        Guid attachment;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
            using var stream = new MemoryStream(new byte[] {137,80,78,71,13,10,26,10});
            attachment = (await scope.ServiceProvider.GetRequiredService<EvidenceService>().UploadAsync(id, owner.Id, owner.Name, 1, new FormFile(stream,0,stream.Length,"file","inspection.png"), "Image to review", true)).Id;
        }
        var denied = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "2", ["SelectedEvidence"] = attachment.ToString() });
        Assert.Contains("permission", await denied.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase); Assert.Equal(0, provider.Calls);
        var local = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "2", ["SelectedEvidence"] = attachment.ToString(), ["ProcessingConsent"] = "true" });
        Assert.Contains("image text was not read", await local.Content.ReadAsStringAsync()); Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task CaseUpdateDuringAiRequestRejectsStaleBriefing()
    {
        var provider = new ReplyHandler("{\"points\":[{\"sourceId\":\"original\",\"quote\":\"" + Original + "\"},{\"sourceId\":\"resolution\",\"quote\":\"" + Report + "\"}]}");
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        provider.BeforeReply = async () => { using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var item = await db.Grievances.SingleAsync(); item.Version++; await db.SaveChangesAsync(); };
        var response = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1", ["UseAi"] = "true", ["ProcessingConsent"] = "true" });
        var html = await response.Content.ReadAsStringAsync(); Assert.Contains("case changed while", html, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("AI-selected source excerpts", html);
    }

    [Fact]
    public async Task LocalBriefIncludesOriginalResolutionHistoryAndCitationsWithoutChangingCase()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var id = await Seed(app);
        var response = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Local source excerpts", html); Assert.Contains(Original, html); Assert.Contains(Report, html);
        Assert.Contains("#brief-source-original", html); Assert.Contains("#brief-source-resolution", html);
        Assert.Contains("Officer reviewed the water supply complaint.", html);
        using var scope = app.Services.CreateScope(); var saved = await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.Include(g => g.History).SingleAsync();
        Assert.Equal(1, saved.Version); Assert.Equal("InProgress", saved.Status); Assert.Single(saved.History);
    }

    [Fact]
    public async Task AuthOwnershipAntiforgeryAndVersionAreEnforced()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync($"/Grievances/Brief/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Grievances/Brief/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/Grievances/Brief/{id}", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var stale = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "0" });
        Assert.Contains("case changed", await stale.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AiRequiresPermissionAndRejectsInventedQuotes()
    {
        var provider = new ReplyHandler("{\"points\":[{\"sourceId\":\"original\",\"quote\":\"Invented claim that the complaint was resolved.\"}]}");
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        var denied = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1", ["UseAi"] = "true" });
        Assert.Equal(0, provider.Calls); Assert.Contains("permission", await denied.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var result = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1", ["UseAi"] = "true", ["ProcessingConsent"] = "true" });
        var html = await result.Content.ReadAsStringAsync(); Assert.Equal(1, provider.Calls);
        Assert.Contains("Local source excerpts", html); Assert.DoesNotContain("Invented claim", html);
        Assert.Contains("original", provider.Body); Assert.Contains("sources", provider.Body);
    }

    [Fact]
    public async Task ValidAiQuotesHaveSourceLinksAndFailureFallsBack()
    {
        var provider = new ReplyHandler("{\"points\":[{\"sourceId\":\"original\",\"quote\":\"" + Original + "\"},{\"sourceId\":\"resolution\",\"quote\":\"" + Report + "\"}]}");
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        var result = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1", ["UseAi"] = "true", ["ProcessingConsent"] = "true" });
        Assert.Contains("AI-selected source excerpts", await result.Content.ReadAsStringAsync());
        provider.Status = HttpStatusCode.ServiceUnavailable;
        var unavailable = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1", ["UseAi"] = "true", ["ProcessingConsent"] = "true" });
        Assert.Contains("Local source excerpts", await unavailable.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingOrForeignSelectedAttachmentDoesNotProcessAnyProviderData()
    {
        var provider = new ReplyHandler("{}"); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); var id = await Seed(app);
        var response = await Post(client, $"/Grievances/Brief/{id}", new() { ["Version"] = "1", ["UseAi"] = "true", ["ProcessingConsent"] = "true", ["SelectedEvidence"] = Guid.NewGuid().ToString() });
        Assert.Contains("supporting files", await response.Content.ReadAsStringAsync()); Assert.Equal(0, provider.Calls);
    }

    private static async Task<Guid> Seed(TestApp app)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
        var item = new Grievance { OwnerId = owner.Id, Reference = "BRIEF-TEST", OriginalText = Original, Title = "Water supply concern", DraftText = Original, Department = "water", District = "Mysuru", Location = "Ward 12", Status = "InProgress", ResolutionReport = Report, CitizenFeedback = "Pending" };
        item.History.Add(new() { Actor = owner.Name, Action = "Officer reviewed the water supply complaint." });
        db.Grievances.Add(item); await db.SaveChangesAsync(); return item.Id;
    }
    private static async Task Setup(HttpClient client) => Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Setup", new() { ["Input.Name"] = "Brief reviewer", ["Input.Email"] = "brief@example.test", ["Input.Password"] = "Brief-Test-Password-72!" })).StatusCode);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string,string> fields)
    {
        var page = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    private sealed class ReplyHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; private set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Func<Task>? BeforeReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(cancellation);
            if (BeforeReply is not null) await BeforeReply();
            var content = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = json } } } });
            return new(Status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }
}
