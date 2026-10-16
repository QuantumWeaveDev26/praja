using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class DraftingTests
{
    [Fact]
    public async Task EmptyChoicesFallsBackToPreservedCitizenStatement()
    {
        var service = Service(new ProviderHandler("{\"choices\":[]}"));
        var result = await service.DraftAsync("No water in Ward 12 for five days.", "English", "Bengaluru Urban", "Ward 12");
        Assert.Contains("No water in Ward 12 for five days.", result.Text);
        Assert.Contains("AI unavailable", result.Method);
    }

    [Fact]
    public async Task SuccessfulProviderReturnsReviewableDraft()
    {
        var service = Service(new ProviderHandler("{\"choices\":[{\"message\":{\"content\":\"{\\\"title\\\":\\\"Water supply interrupted\\\",\\\"draft\\\":\\\"Water supply has been interrupted in Ward 12 for five days. Please investigate.\\\",\\\"summary\\\":\\\"Five-day water interruption in Ward 12.\\\"}\"}}]}"));
        var result = await service.DraftAsync("No water in Ward 12 for five days.", "English", "Bengaluru Urban", "Ward 12");
        Assert.Equal("Water supply interrupted", result.Title);
        Assert.Contains("officer review required", result.Method);
    }

    [Fact]
    public async Task OversizedProviderReplyIsStoppedBeforeFullBuffering()
    {
        var content = new CountingContent();
        var service = Service(new ProviderHandler(content));
        var result = await service.DraftAsync("No water in Ward 12 for five days.", "English", "Bengaluru Urban", "Ward 12");
        Assert.Contains("AI unavailable", result.Method);
        Assert.True(content.Stream.BytesRead <= 108192, $"Read {content.Stream.BytesRead} bytes before rejecting the reply.");
    }

    [Fact]
    public async Task StalledBodyFallsBackBeforeCallerAbandonsRequest()
    {
        var service = Service(new ProviderHandler(new StreamContent(new StallingStream())));
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await service.DraftAsync("No water in Ward 12 for five days.", "English", "Bengaluru Urban", "Ward 12", caller.Token);
        Assert.Contains("AI unavailable", result.Method);
        Assert.False(caller.IsCancellationRequested);
    }

    private static DraftingService Service(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Ai:ApiKey"] = "test-only", ["Ai:Endpoint"] = "https://provider.example.test/chat/completions", ["Ai:Model"] = "test-model", ["Ai:TimeoutSeconds"] = "1" }).Build();
        return new DraftingService(config, new ClientFactory(handler), NullLogger<DraftingService>.Instance);
    }
    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class ProviderHandler : HttpMessageHandler
    {
        private readonly HttpContent content;
        public ProviderHandler(string json) => content = new StringContent(json);
        public ProviderHandler(HttpContent response) => content = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
    private sealed class CountingContent : HttpContent
    {
        public CountingStream Stream { get; } = new();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Stream);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await Stream.CopyToAsync(stream);
    }
    private class CountingStream : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 500000;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, 500000 - BytesRead); Array.Fill(buffer, (byte)'x', offset, read); BytesRead += read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class StallingStream : CountingStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return 0;
        }
    }
}
