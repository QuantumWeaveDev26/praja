using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;

public sealed record NewsFeed(string Id, string Name, string Url, string ArticleHost);
public sealed record PublicCollectionResult(int Examined, int Retained, int Existing, int Skipped, string Status);

/// <summary>Bounded, explicit collection of public metadata. Never downloads articles, videos or images.</summary>
public sealed class PublicSourceCollection(IHttpClientFactory clients, PublicSourceSettings settings, InboxService inbox, WorkspaceDb db, TimeProvider clock)
{
    public const string YouTubeActor = "YouTube public search";
    public const string NewsActor = "News feed collection";
    public static readonly string[] LocalReviewActors = [XCollectionService.Actor, YouTubeActor, NewsActor];
    public static bool UsesLocalReview(string? actor) => actor is not null && LocalReviewActors.Contains(actor);
    public static IReadOnlyList<NewsFeed> Feeds { get; } = Array.AsReadOnly(new[]
    {
        new NewsFeed("hindu-karnataka", "The Hindu · Karnataka", "https://www.thehindu.com/news/national/karnataka/feeder/default.rss", "www.thehindu.com"),
        new NewsFeed("citizen-matters", "Citizen Matters · city reporting", "https://citizenmatters.in/feed/", "citizenmatters.in")
    });
    private sealed record Entry(string Text, string Url, string Id);
    private sealed class SourceFailure(string message) : Exception(message);

    public async Task<PublicCollectionResult> RunAsync(Guid owner, string source, Guid version, CancellationToken cancellation = default)
    {
        var feed = Feeds.SingleOrDefault(f => f.Id == source);
        if (source != "youtube" && feed is null) throw new InvalidOperationException("Choose a listed public source.");
        var gate = settings.RunLock(owner);
        if (!await gate.WaitAsync(0, cancellation)) throw new InvalidOperationException("A collection or settings change is running. Try again after it finishes.");
        try
        {
            var config = settings.Read(owner);
            if (config.Version != version) throw new InvalidOperationException("Settings or results changed. Reload before collecting.");
            if (source == "youtube" && config.YouTubeKey.Length == 0) throw new InvalidOperationException("Save a YouTube Data API key first. News feeds do not need a key.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var label = feed?.Name ?? "YouTube";
            try
            {
                var url = feed?.Url ?? "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&maxResults=10&order=date&regionCode=IN&publishedAfter=" +
                    Uri.EscapeDataString(clock.GetUtcNow().AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)) + "&q=" + Uri.EscapeDataString(config.Query);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // Keep the credential out of request URLs, logs and provider errors.
                if (source == "youtube") request.Headers.Add("X-Goog-Api-Key", config.YouTubeKey);
                request.Headers.UserAgent.ParseAdd("Praja-Public-Intake/1.0");
                using var response = await clients.CreateClient("PublicSources").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) throw new SourceFailure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when source == "youtube" => "YouTube denied access. Check the API key, enable YouTube Data API v3, and check key restrictions and quota.",
                    HttpStatusCode.TooManyRequests => label + " rate limit reached. Retry manually later.",
                    _ => label + " is unavailable or denied this request. No automatic retry was made."
                });
                var bytes = await ReadBounded(response.Content, timeout.Token);
                var entries = feed is null ? ParseYouTube(bytes) : ParseFeed(bytes, feed);
                var examined = entries.Count;
                // Feeds are general reporting: every query word must occur in the supplied title/summary.
                var words = config.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var matching = feed is null ? entries : entries.Where(e => words.All(w => e.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
                var retained = 0; var existing = 0; var skipped = entries.Count - matching.Count;
                var actor = feed is null ? YouTubeActor : NewsActor;
                await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
                foreach (var entry in matching.Take(10))
                {
                    if (await db.InboxMessages.AnyAsync(m => m.OwnerId == owner && m.ExternalId == entry.Id && m.CapturedBy == actor, timeout.Token)) { existing++; continue; }
                    await inbox.CaptureAsync(owner, actor, entry.Text, feed is null ? "Social media" : "News/RSS",
                        entry.Text.Any(c => c is >= '\u0c80' and <= '\u0cff') ? "Kannada" : "English", entry.Url, entry.Id, true, timeout.Token);
                    retained++;
                }
                await transaction.CommitAsync(timeout.Token);
                var status = $"{label}: {examined} supported entries examined; {retained} retained for review; {existing} already retained; {skipped} did not match the feed query. At most 10 matches handled. No case registered.";
                settings.Update(owner, s => s with { Version = Guid.NewGuid(), Status = status });
                return new(examined, retained, existing, skipped, status);
            }
            catch (Exception ex) when (ex is SourceFailure or HttpRequestException or JsonException or XmlException or IOException or UnauthorizedAccessException or DbUpdateException or OperationCanceledException)
            {
                var message = ex is SourceFailure known ? known.Message : ex is OperationCanceledException ? "Collection was cancelled or timed out. Retry manually; source IDs prevent duplicate retention." : "Collection could not finish. Check connectivity and local storage. Invalid or oversized responses are not imported.";
                try { settings.Update(owner, s => s with { Status = message }); }
                catch (Exception save) when (save is IOException or UnauthorizedAccessException) { }
                if (cancellation.IsCancellationRequested) cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException(message);
            }
        }
        finally { gate.Release(); }
    }

    private static async Task<byte[]> ReadBounded(HttpContent content, CancellationToken token)
    {
        const int limit = 1_000_000;
        if (content.Headers.ContentLength > limit) throw new SourceFailure("Source response is too large. Nothing imported.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > limit) throw new SourceFailure("Source response is too large. Nothing imported.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
    private static List<Entry> ParseYouTube(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 20 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 10)
            throw new SourceFailure("YouTube returned an invalid response. Nothing imported.");
        var result = new List<Entry>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("snippet", out var snippet) || snippet.ValueKind != JsonValueKind.Object)
                throw new SourceFailure("YouTube returned incomplete video metadata. Nothing imported.");
            var id = String(identity, "videoId"); var title = WebUtility.HtmlDecode(String(snippet, "title"));
            if (id.Length != 11 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) || string.IsNullOrWhiteSpace(title))
                throw new SourceFailure("YouTube returned invalid video metadata. Nothing imported.");
            var text = "YouTube video title: " + title + "\nChannel: " + WebUtility.HtmlDecode(String(snippet, "channelTitle")) +
                "\nPublished: " + String(snippet, "publishedAt") + "\nDescription excerpt: " + WebUtility.HtmlDecode(String(snippet, "description"));
            result.Add(new(Limit(text, 6000), "https://www.youtube.com/watch?v=" + id, "youtube:" + id));
        }
        return result;
    }
    private static string String(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";

    private static List<Entry> ParseFeed(byte[] bytes, NewsFeed feed)
    {
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root is null || root.Name.LocalName is not ("rss" or "feed")) throw new SourceFailure("Source did not return an RSS or Atom feed. Nothing imported.");
        var result = new List<Entry>();
        var atom = root.Name.LocalName == "feed";
        var container = atom ? root : root.Element("channel") ?? throw new SourceFailure("Invalid RSS feed. Nothing imported.");
        foreach (var item in container.Elements().Where(e => e.Name.LocalName == (atom ? "entry" : "item")).Take(100))
        {
            string Field(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
            var link = atom ? item.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && (e.Attribute("rel")?.Value is null or "alternate"))?.Attribute("href")?.Value ?? "" : Field("link");
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Host != feed.ArticleHost || link.Length > 1000) continue;
            var title = Plain(Field("title"));
            if (title.Length == 0) continue;
            var summary = Plain(atom ? Field("summary") : Field("description"));
            // Keep short supplied excerpts with attribution; do not fetch the full article or embedded resources.
            var text = feed.Name + "\nHeadline: " + Limit(title, 500) + "\nPublished: " + Limit(Plain(atom ? Field("published") : Field("pubDate")), 100) +
                "\nFeed excerpt: " + Limit(summary, 1000);
            var canonical = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
            var id = "news:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            result.Add(new(text, canonical, id));
        }
        return result;
    }
    private static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))).Trim();
    private static string Limit(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
