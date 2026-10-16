using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Praja.Web.Domain;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class IntakeAnalysisTests
{
    private const string Original = "Taps have been dry in Ward 12, Mysuru, for three days. Please restore supply.";
    private static Dictionary<string, object> Reply() => new()
    {
        ["district"] = "Mysuru", ["location"] = "Ward 12", ["timeline"] = "three days", ["requestedAction"] = "Please restore supply",
        ["department"] = "water", ["routingEvidence"] = "Taps have been dry", ["needsRoutingReview"] = false, ["language"] = "English"
    };

    [Fact]
    public async Task UnsupportedFactsAreRemovedWhileSupportedFactsRemain()
    {
        var reply = Reply(); reply["district"] = "Bengaluru Urban"; reply["timeline"] = "2026-10-10";
        var result = await Service(new Provider(JsonSerializer.Serialize(reply))).AnalyzeAsync(Original, true);
        Assert.Empty(result.District); Assert.Empty(result.Timeline); Assert.True(result.RejectedUnsupportedFields);
        Assert.Equal("Ward 12", result.Location); Assert.Equal("water", result.Route.Department);
    }

    [Theory]
    [InlineData("invented-department", "Taps have been dry", false)]
    [InlineData("water", "Invented service evidence", false)]
    [InlineData("water", "Taps have been dry", true)]
    public async Task InvalidOrUncertainRoutingRequiresManualSelection(string department, string excerpt, bool uncertain)
    {
        var reply = Reply(); reply["department"] = department; reply["routingEvidence"] = excerpt; reply["needsRoutingReview"] = uncertain;
        var result = await Service(new Provider(JsonSerializer.Serialize(reply))).AnalyzeAsync(Original, true);
        Assert.Equal("review", result.Route.Department); Assert.True(result.Route.NeedsManualReview);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"district\":42}")]
    [InlineData("{}")]
    public async Task MalformedProviderOutputFallsBackWithoutInventingLocation(string json)
    {
        var result = await Service(new Provider(json)).AnalyzeAsync("No water supply in Ward 12 for three days.", true);
        Assert.Contains("AI unavailable", result.Method); Assert.Equal("water", result.Route.Department);
        Assert.Empty(result.District); Assert.Empty(result.Location);
    }

    [Fact]
    public async Task LocalModeOffersOnlyKeywordRouting()
    {
        var config = new ConfigurationBuilder().Build();
        var result = await new DraftingService(config, new Factory(new Provider("{}")), NullLogger<DraftingService>.Instance)
            .AnalyzeAsync("No water supply in Ward 12 for three days.", true);
        Assert.Contains("AI not connected", result.Method); Assert.Equal("water", result.Route.Department);
        Assert.Empty(result.District); Assert.Empty(result.Location); Assert.Empty(result.Timeline);
    }

    [Fact]
    public async Task KannadaFactsRetainOriginalSpelling()
    {
        const string text = "ಮೈಸೂರು ವಾರ್ಡ್ 12 ನಲ್ಲಿ ಮೂರು ದಿನಗಳಿಂದ ನೀರು ಬರುತ್ತಿಲ್ಲ. ನೀರು ಸರಬರಾಜು ಮಾಡಿ.";
        var reply = Reply(); reply["district"] = "ಮೈಸೂರು"; reply["location"] = "ವಾರ್ಡ್ 12"; reply["timeline"] = "ಮೂರು ದಿನಗಳಿಂದ";
        reply["requestedAction"] = "ನೀರು ಸರಬರಾಜು ಮಾಡಿ"; reply["routingEvidence"] = "ನೀರು ಬರುತ್ತಿಲ್ಲ"; reply["language"] = "Kannada";
        var result = await Service(new Provider(JsonSerializer.Serialize(reply))).AnalyzeAsync(text, true);
        Assert.Equal("ಮೈಸೂರು", result.District); Assert.Equal("ವಾರ್ಡ್ 12", result.Location); Assert.Equal("Kannada", result.Language);
        Assert.False(result.RejectedUnsupportedFields);
    }

    [Fact]
    public async Task ProviderReceivesReadableKannadaInsideCitizenMessage()
    {
        var provider = new Provider(JsonSerializer.Serialize(Reply()));
        await Service(provider).AnalyzeAsync("ಮೈಸೂರು ವಾರ್ಡ್ 12 ನಲ್ಲಿ ಮೂರು ದಿನಗಳಿಂದ ನೀರು ಬರುತ್ತಿಲ್ಲ.", true);
        Assert.Contains("ಮೈಸೂರು", provider.UserMessage);
    }

    [Fact]
    public async Task AcceptedFactRetainsSourceCasing()
    {
        var reply = Reply(); reply["district"] = "mysuru";
        var result = await Service(new Provider(JsonSerializer.Serialize(reply))).AnalyzeAsync(Original, true);
        Assert.Equal("Mysuru", result.District);
    }

    [Fact]
    public async Task OversizedOrFailedProviderOutputFallsBack()
    {
        var oversized = await Service(new Provider(new string('x', 100001))).AnalyzeAsync(Original, true);
        Assert.Contains("AI unavailable", oversized.Method);
        var failed = await Service(new Provider("{}", HttpStatusCode.Unauthorized)).AnalyzeAsync(Original, true);
        Assert.Contains("AI unavailable", failed.Method);
    }

    [Fact]
    public async Task ConsentAndInputValidationPrecedeProviderRequest()
    {
        var provider = new Provider("{}"); var service = Service(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(Original, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync("short", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(new string('x', 6001), true));
        Assert.Equal(0, provider.Calls);
    }
    [Theory]
    [InlineData("water_supply","no_supply",true)]
    [InlineData("road_condition","potholes",false)]
    [InlineData("water_supply","potholes",false)]
    [InlineData("invented_government_code","",false)]
    public async Task AiTaxonomyMustBelongToTheSuggestedService(string category,string issue,bool valid)
    {
        var reply=Reply();reply["category"]=category;reply["subcategory"]=issue;
        var provider=new Provider(JsonSerializer.Serialize(reply));var result=await Service(provider).AnalyzeAsync(Original,true);
        Assert.Equal("water",result.Route.Department);Assert.Equal(valid?category:"",result.Route.Category);Assert.Equal(valid?issue:"",result.Route.Subcategory);
        Assert.Equal(!valid,result.RejectedUnsupportedFields);Assert.Equal("AI intake",result.Route.Method);
        Assert.Contains("demoCategories",provider.UserMessage);Assert.Contains("demoIssues",provider.UserMessage);
    }
    [Fact]
    public async Task AmbiguousAiRouteRetainsEvidenceButClearsSpecificCategory()
    {
        var reply=Reply();reply["category"]="water_supply";reply["subcategory"]="no_supply";reply["needsRoutingReview"]=true;
        var result=await Service(new Provider(JsonSerializer.Serialize(reply))).AnalyzeAsync(Original,true);
        Assert.Equal("review",result.Route.Department);Assert.Equal("",result.Route.Category);Assert.Equal("",result.Route.Subcategory);Assert.Contains(result.Route.Evidence,Original);
    }
    [Fact]
    public async Task WrongJsonCategoryShapeIsRemovedInsteadOfBecomingASavedCode()
    {
        var reply=Reply();reply["category"]=new[]{"water_supply"};reply["subcategory"]="no_supply";
        var result=await Service(new Provider(JsonSerializer.Serialize(reply))).AnalyzeAsync(Original,true);
        Assert.True(result.RejectedUnsupportedFields);Assert.Equal("",result.Route.Category);Assert.Equal("",result.Route.Subcategory);
    }

    [Fact]
    public void ReceiptsExpireAndCannotMoveBetweenOfficersOrStatements()
    {
        var clock = new Clock(); var service = new IntakeReviewService(new EphemeralDataProtectionProvider(), clock);
        var owner = Guid.NewGuid();
        var analysis = new IntakeAnalysis("Mysuru", "Ward 12", "three days", "", "English", new RoutingSuggestion("water", "review", false)
            {Category="water_supply",Subcategory="no_supply",Evidence="Taps have been dry",Method="AI intake",Candidates=["water"]}, "AI", false);
        var token = service.Protect(owner, Original, analysis);
        Assert.Equal(JsonSerializer.Serialize(analysis), JsonSerializer.Serialize(service.Read(token, owner, Original)));
        Assert.Throws<InvalidOperationException>(() => service.Read(token, Guid.NewGuid(), Original));
        Assert.Throws<InvalidOperationException>(() => service.Read(token, owner, "Different citizen statement"));
        clock.Now = clock.Now.AddMinutes(30);
        Assert.Throws<InvalidOperationException>(() => service.Read(token, owner, Original));
    }

    private static DraftingService Service(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Ai:ApiKey"] = "test-only", ["Ai:Endpoint"] = "https://provider.example.test/chat/completions", ["Ai:Model"] = "test-model", ["Ai:TimeoutSeconds"] = "1" }).Build();
        return new DraftingService(config, new Factory(handler), NullLogger<DraftingService>.Instance);
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Provider(string result, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string UserMessage { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            UserMessage = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            return new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = result } } } })) };
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
