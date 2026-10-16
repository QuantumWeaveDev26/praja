using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Praja.Web.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Praja.Tests;
public class MediaIntakeTests
{
    [Fact]
    public async Task TextPetitionPreservesKannadaContent()
    {
        const string text = "ನಮ್ಮ ವಾರ್ಡ್‌ನಲ್ಲಿ ನೀರಿನ ಸರಬರಾಜು ಐದು ದಿನಗಳಿಂದ ಸ್ಥಗಿತವಾಗಿದೆ. ದಯವಿಟ್ಟು ಪರಿಶೀಲಿಸಿ.";
        var result = await Service().ExtractAsync(File("petition.txt", Encoding.UTF8.GetBytes(text)), "Kannada", true);
        Assert.Equal(text, result.Text);
        Assert.Contains("Local", result.Method);
    }

    [Fact]
    public async Task PdfPetitionExtractsReadableTextLocally()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Water supply has stopped in Ward 12 for five days.", 12, new PdfPoint(30, 700), font);
        var result = await Service().ExtractAsync(File("petition.pdf", builder.Build()), "English", true);
        Assert.Contains("Water supply has stopped", result.Text);
        Assert.Contains("Local PDF", result.Method);
    }

    [Fact]
    public async Task DisguisedExecutableIsRejectedWithoutProviderCall()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ExtractAsync(File("petition.pdf", "MZ-not-a-PDF"u8.ToArray()), "English", true));
    }

    [Fact]
    public async Task FileSizeIsEnforcedBeforeReading()
    {
        var file = new FormFile(Stream.Null, 0, 8 * 1024 * 1024 + 1, "document", "petition.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ExtractAsync(file, "English", true));
    }

    [Fact]
    public async Task ConsentIsRequiredBeforeProcessing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ExtractAsync(File("petition.txt", "A citizen complaint with sufficient detail."u8.ToArray()), "English", false));
    }

    [Fact]
    public async Task UnconfiguredSpeechDoesNotPretendToTranscribe()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().TranscribeAsync(File("voice.wav", Wave()), "English", true));
    }

    [Fact]
    public async Task SpeechResponseProducesEditableTranscript()
    {
        var handler = new ReplyHandler("{\"text\":\"Water supply has stopped in Ward 12 for five days.\"}");
        var result = await Service(handler).TranscribeAsync(File("voice.wav", Wave()), "English", true);
        Assert.Equal("Water supply has stopped in Ward 12 for five days.", result.Text);
        Assert.Contains("review", result.Method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VisionResponseProducesReviewableOcrText()
    {
        var handler = new ReplyHandler("{\"choices\":[{\"message\":{\"content\":\"Water supply has stopped in Ward 12 for five days.\"}}]}");
        var result = await Service(handler).ExtractAsync(File("petition.png", [137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82]), "English", true);
        Assert.Contains("Ward 12", result.Text);
        Assert.Contains("OCR", result.Method);
    }

    [Fact]
    public async Task LongDocumentsAreRejectedWithoutSilentTruncation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ExtractAsync(File("petition.txt", Encoding.UTF8.GetBytes(new string('a', 6001))), "English", true));
    }
    private static IFormFile File(string name, byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "document", name);
    private static byte[] Wave() => "RIFF0123WAVE012345678901234567890123"u8.ToArray();
    private static MediaIntakeService Service(HttpMessageHandler? handler = null)
    {
        var settings = handler is null ? new Dictionary<string,string?>() : new Dictionary<string,string?>
        { ["Ai:ApiKey"]="test-only", ["Ai:Endpoint"]="https://provider.example.test/chat/completions", ["Ai:Model"]="test-vision", ["Ai:TranscriptionEndpoint"]="https://provider.example.test/audio/transcriptions", ["Ai:TranscriptionModel"]="test-speech" };
        return new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new Factory(handler));
    }
    private sealed class Factory(HttpMessageHandler? handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler is null ? throw new InvalidOperationException("No provider call expected.") : new(handler, disposeHandler:false);
    }
    private sealed class ReplyHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }
}
