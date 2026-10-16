using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Xunit;

namespace Praja.Tests;

public class StructuredIntakeFlowTests
{
    private const string Original = "Taps have been dry in Ward 12, Mysuru, for three days. Please restore supply.";

    [Fact]
    public async Task SuggestionsFillMissingFieldsWithoutCreatingCaseAndSurviveDraftPreparation()
    {
        using var provider = new Provider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        await Setup(client);
        var fields = Fields();
        var html = await Suggest(client, fields);
        Assert.Equal("Mysuru", Value(html, "Input.District"));
        Assert.Equal("Ward 12", Value(html, "Input.Location"));
        Assert.Contains("three days", html);
        var receipt = Value(html, "AnalysisToken"); Assert.NotEmpty(receipt);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
        Assert.Empty(await db.Grievances.ToListAsync());
        fields["Input.District"] = "Mysuru"; fields["Input.Location"] = "Ward 12";
        fields["AnalysisToken"] = receipt; fields["__RequestVerificationToken"] = Token(html);
        var draft = await client.PostAsync("/Grievances/New", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, draft.StatusCode);
        var saved = await db.Grievances.Include(g => g.History).SingleAsync();
        Assert.Equal("water", saved.Department); // No old keyword match in this complaint.
        Assert.Contains("AI", saved.RoutingReason); Assert.Equal(Original, saved.OriginalText);
        Assert.Equal("Draft", saved.Status);
        Assert.Contains(saved.History, h => h.Action.Contains("intake suggestions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SuggestionsPreserveOfficerEnteredLocation()
    {
        using var provider = new Provider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); fields["Input.District"] = "Officer district"; fields["Input.Location"] = "Officer locality";
        var html = await Suggest(client, fields);
        Assert.Equal("Officer district", Value(html, "Input.District"));
        Assert.Equal("Officer locality", Value(html, "Input.Location"));
    }

    [Fact]
    public async Task ConsentIsRequiredBeforeAnalysisReachesProvider()
    {
        using var provider = new Provider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); fields["Input.Consent"] = "false";
        var html = await Suggest(client, fields);
        Assert.Equal(0, provider.Calls); Assert.Empty(Value(html, "AnalysisToken"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TamperedOrStaleAnalysisCannotCreateCase(bool tamper)
    {
        using var provider = new Provider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); var html = await Suggest(client, fields);
        var receipt = Value(html, "AnalysisToken"); Assert.NotEmpty(receipt);
        fields["Input.District"] = "Mysuru"; fields["Input.Location"] = "Ward 12";
        fields["AnalysisToken"] = tamper ? receipt[..^12] + "XXXXXXXXXXXX" : receipt;
        if (!tamper) fields["Input.OriginalText"] = "Garbage has not been collected in Ward 12 for three days.";
        fields["__RequestVerificationToken"] = Token(html);
        var response = await client.PostAsync("/Grievances/New", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
    }

    [Fact]
    public async Task EmptyChangedComplaintShowsReviewErrorInsteadOfServerError()
    {
        using var provider = new Provider(); using var app = new TestApp(provider);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client);
        var fields = Fields(); var html = await Suggest(client, fields);
        fields["AnalysisToken"] = Value(html, "AnalysisToken"); fields["Input.OriginalText"] = "";
        fields["__RequestVerificationToken"] = Token(html);
        var response = await client.PostAsync("/Grievances/New", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.ToListAsync());
    }

    private static Dictionary<string, string> Fields() => new()
    {
        ["Input.OriginalText"] = Original, ["Input.Language"] = "English", ["Input.Source"] = "Text", ["Input.Consent"] = "true"
    };
    private static async Task<string> Suggest(HttpClient client, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = Token(await client.GetStringAsync("/Grievances/New"));
        var response = await client.PostAsync("/Grievances/New?handler=Analyze", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await response.Content.ReadAsStringAsync();
    }
    private static async Task Setup(HttpClient client)
    {
        var fields = new Dictionary<string, string> { ["Input.Name"] = "Intake reviewer", ["Input.Email"] = "structured@example.test", ["Input.Password"] = "Structured-Test-Password-72!", ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/Setup")) };
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Setup", new FormUrlEncodedContent(fields))).StatusCode);
    }
    private static string Token(string html) => Value(html, "__RequestVerificationToken");
    private static string Value(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private sealed class Provider : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var content = Calls == 1 ? JsonSerializer.Serialize(new
            {
                district = "Mysuru", location = "Ward 12", timeline = "three days", requestedAction = "Please restore supply",
                department = "water", routingEvidence = "Taps have been dry", needsRoutingReview = false, language = "English"
            }) : JsonSerializer.Serialize(new { title = "Taps dry in Ward 12", draft = Original, summary = "Dry taps for three days." });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }))
            });
        }
    }
}
