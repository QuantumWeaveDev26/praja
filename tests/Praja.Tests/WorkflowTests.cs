using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Praja.Tests;

public sealed class TestApp(HttpMessageHandler? provider = null) : WebApplicationFactory<Program>
{
    private readonly string database = Path.Combine(Path.GetTempPath(), $"praja-test-{Guid.NewGuid():N}.db");
    public string SettingsDirectory => database + ".settings";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:Cases"] = $"Data Source={database};Pooling=False",
            ["Database:BackupDirectory"] = Path.Combine(SettingsDirectory,"backups"),
            ["SeedDemo"] = "false",
            ["Ai:SettingsDirectory"] = SettingsDirectory,
            ["Ai:ApiKey"] = provider is null ? "" : "test-only", ["Ai:Endpoint"] = provider is null ? "" : "https://provider.example.test/chat/completions", ["Ai:Model"] = provider is null ? "" : "test-model",
            ["Ai:TranscriptionEndpoint"] = provider is null ? "" : "https://provider.example.test/audio/transcriptions", ["Ai:TranscriptionModel"] = provider is null ? "" : "test-speech"
        }));
        if (provider is not null) builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new ProviderFactory(provider));
        });
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(database)) File.Delete(database);
        if (disposing && Directory.Exists(SettingsDirectory) && Path.GetFullPath(SettingsDirectory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(SettingsDirectory, true);
    }
    private sealed class ProviderFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

public class WorkflowTests
{
    [Fact]
    public async Task AnonymousUserCannotReadCaseApi()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cases")).StatusCode);
    }

    [Fact]
    public async Task SessionFromAnotherDatabaseCannotAuthenticate()
    {
        using var first = new TestApp();
        using var firstClient = first.CreateClient(new() { AllowAutoRedirect = false });
        var setup = await SetUp(firstClient);
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("Praja.Session="));
        using var second = new TestApp();
        using var secondClient = second.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        secondClient.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondClient.GetAsync("/api/cases")).StatusCode);
    }

    [Fact]
    public async Task PasswordChangeInvalidatesAnExistingSession()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await SetUp(client);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Praja.Web.Data.WorkspaceDb>();
            var user = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Operators);
            user.PasswordHash = Praja.Web.Services.WorkspaceIdentity.Hasher.HashPassword(user, "New-Password-For-Test-82!");
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cases")).StatusCode);
    }

    [Fact]
    public async Task MutationWithoutCsrfTokenIsRejected()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await SetUp(client);
        var response = await client.PostAsync("/Grievances/New", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.OriginalText"] = "Test grievance" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ComplaintPersistsAndCannotCloseWithoutEvidenceAndCitizenConfirmation()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await SetUp(client);
        var response = await Post(client, "/Grievances/New", new()
        {
            ["Input.OriginalText"] = "There has been no drinking water supply in Ward 12 for the past five days.",
            ["Input.District"] = "Bengaluru Urban", ["Input.Location"] = "Ward 12", ["Input.Language"] = "English",
            ["Input.Source"] = "Text", ["Input.Consent"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var path = response.Headers.Location!.ToString();
        var id = path.Split('/').Last();
        var record = await Read(client, id);
        Assert.Equal("Draft", record.GetProperty("status").GetString());
        Assert.Contains("five days", record.GetProperty("originalText").GetString());

        await Post(client, path, new()
        {
            ["Version"] = "1", ["Title"] = "Water supply interrupted in Ward 12", ["DraftText"] = "Water supply has stopped for five days in Ward 12. Please investigate and restore supply.",
            ["Department"] = "water", ["Reviewed"] = "true"
        }, "Register");
        Assert.Equal("Registered", (await Read(client, id)).GetProperty("status").GetString());
        await Post(client, path, new() { ["Version"] = "2" }, "Start");
        await Post(client, path, new()
        {
            ["Version"] = "3", ["ResolutionReport"] = "The damaged water valve was replaced and the water supply was restored in Ward 12. The inspection team checked supply at three households.",
            ["EvidenceReference"] = "Inspection report WR-12", ["CitizenFeedback"] = "Pending"
        }, "Review");
        await Post(client, path, new() { ["Version"] = "4", ["Reviewed"] = "true" }, "Close");
        Assert.Equal("ClosureReview", (await Read(client, id)).GetProperty("status").GetString());
        await Post(client, path, new()
        {
            ["Version"] = "4", ["ResolutionReport"] = "The damaged water valve was replaced and the water supply was restored in Ward 12. The inspection team checked supply at three households.",
            ["EvidenceReference"] = "Inspection report WR-12", ["CitizenFeedback"] = "Confirmed"
        }, "Review");
        await Post(client, path, new() { ["Version"] = "5", ["Reviewed"] = "true" }, "Close");
        Assert.Equal("Closed", (await Read(client, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExistingWorkspaceCannotBeTakenOverThroughSetup()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await SetUp(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Setup")).StatusCode);
    }

    private static async Task<HttpResponseMessage> SetUp(HttpClient client)
    {
        var response = await Post(client, "/Setup", new()
        {
            ["Input.Name"] = "Test Officer", ["Input.Email"] = "officer@example.test", ["Input.Password"] = "Test-Workspace-Password-72!"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields, string? handler = null)
    {
        var page = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token);
        return await client.PostAsync(path + (handler is null ? "" : "?handler=" + handler), new FormUrlEncodedContent(fields));
    }
    private static async Task<JsonElement> Read(HttpClient client, string id)
    {
        var response = await client.GetAsync("/api/cases/" + id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
