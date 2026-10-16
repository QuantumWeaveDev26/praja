using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class AiSettingsFlowTests
{
    [Fact]
    public async Task OpenAiSettingsRequireOfficerAuthentication()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/AiSettings");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task SavedKeyIsEncryptedNeverEchoedAndCanBeRetainedOrDisabled()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client);
        const string key = "sk-synthetic-key-for-settings-test-only";
        var saved = await Save(client, key, "gpt-4o-mini");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var html = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, html);
        Assert.Contains("Configuration saved", html);
        var files = Directory.GetFiles(app.SettingsDirectory, "*.protected");
        Assert.Single(files);
        Assert.DoesNotContain(key, await File.ReadAllTextAsync(files[0]));
        using (var scope = app.Services.CreateScope())
        {
            Assert.True(scope.ServiceProvider.GetRequiredService<DraftingService>().IsConnected);
            Assert.True(scope.ServiceProvider.GetRequiredService<MediaIntakeService>().SpeechConfigured);
        }
        var invalid = await Save(client, key, "bad model with spaces");
        Assert.DoesNotContain(key, await invalid.Content.ReadAsStringAsync());
        Assert.Contains("model ID", await invalid.Content.ReadAsStringAsync());
        var updated = await Save(client, "", "gpt-4o-mini");
        Assert.Contains("Configuration saved", await updated.Content.ReadAsStringAsync());
        using (var scope = app.Services.CreateScope())
            Assert.True(scope.ServiceProvider.GetRequiredService<DraftingService>().IsConnected);
        var disabled = await client.PostAsync("/AiSettings?handler=Disable", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = await Token(client, "/AiSettings") }));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.False(scope.ServiceProvider.GetRequiredService<DraftingService>().IsConnected);
    }

    [Fact]
    public async Task SavingSettingsWithoutCsrfIsRejected()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/AiSettings?handler=Save", new FormUrlEncodedContent(new Dictionary<string,string> { ["Input.ApiKey"] = "test" }))).StatusCode);
    }

    [Fact]
    public async Task TestButtonSendsSyntheticTextAndCreatesNoCase()
    {
        using var root = new TestApp();
        var handler = new SampleProvider();
        using var app = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IHttpClientFactory>(new Factory(handler))));
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client);
        await Save(client, "sk-synthetic-key-never-real", "gpt-4o-mini");
        var reply = await client.PostAsync("/AiSettings?handler=Test", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = await Token(client, "/AiSettings") }));
        Assert.Contains("Drafting test passed", await reply.Content.ReadAsStringAsync());
        Assert.Contains("Synthetic connection test", handler.Body);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Url);
        using var scope = app.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.CountAsync());
    }

    [Fact]
    public async Task CredentialsCopiedFromAnotherWorkspaceAreRejected()
    {
        using var first = new TestApp();
        using var client = first.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client); await Save(client, "sk-synthetic-private-first", "gpt-4o-mini");
        using var second = new TestApp();
        using var secondClient = second.CreateClient();
        await secondClient.GetAsync("/Citizen");
        var config = second.Services.GetRequiredService<IConfiguration>();
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config["Database:Provider"] + "|" + config.GetConnectionString("Cases"))));
        Directory.CreateDirectory(second.SettingsDirectory);
        File.Copy(Directory.GetFiles(first.SettingsDirectory, "*.protected").Single(), Path.Combine(second.SettingsDirectory, "ai-" + scope + ".protected"));
        Assert.False(second.Services.GetRequiredService<AiConfigurationStore>().Read().DraftingConfigured);
        var file = Directory.GetFiles(first.SettingsDirectory, "*.protected").Single();
        await File.WriteAllTextAsync(file, "tampered");
        Assert.False(first.Services.GetRequiredService<AiConfigurationStore>().Read().DraftingConfigured);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private sealed class SampleProvider : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public string Url { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellation); Url = request.RequestUri!.ToString();
            var text = JsonSerializer.Serialize(new { title = "Water supply grievance", draft = "Please investigate the interruption of water supply in Sample Ward.", summary = "Water supply interrupted." });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } })) };
        }
    }

    private static async Task<HttpResponseMessage> Save(HttpClient client, string key, string model) => await client.PostAsync("/AiSettings?handler=Save", new FormUrlEncodedContent(new Dictionary<string,string>
    {
        ["__RequestVerificationToken"] = await Token(client, "/AiSettings"), ["Input.ApiKey"] = key,
        ["Input.Model"] = model, ["Input.SpeechModel"] = "gpt-4o-mini-transcribe", ["Input.EnableSpeech"] = "true"
    }));
    private static async Task Setup(HttpClient client) => await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string,string>
    {
        ["__RequestVerificationToken"] = await Token(client, "/Setup"), ["Input.Name"] = "Test officer", ["Input.Email"] = "settings@example.test", ["Input.Password"] = "Settings-Test-Password-72!"
    }));
    private static async Task<string> Token(HttpClient client, string path) => WebUtility.HtmlDecode(Regex.Match(await client.GetStringAsync(path), "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
}
