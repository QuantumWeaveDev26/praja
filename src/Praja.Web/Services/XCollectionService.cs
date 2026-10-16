using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;

public sealed record XCollectionResult(int Received, int Retained, int Existing, int Skipped, bool HasNextPage, string Status);

/// <summary>One explicitly requested, bounded public search. Never invokes an AI provider.</summary>
public sealed class XCollectionService(IHttpClientFactory clients, XConfigurationStore store, InboxService inbox, WorkspaceDb db, TimeProvider clock)
{
    public const string Actor = "X public search";
    public async Task<XCollectionResult> RunAsync(Guid owner, bool nextPage = false, CancellationToken cancellation = default, Guid? expectedVersion = null)
    {
        var gate = store.RunLock(owner);
        if (!await gate.WaitAsync(0, cancellation)) throw new InvalidOperationException("An X search or settings change is already running. Try again after it finishes.");
        try
        {
            var config = store.Read(owner);
            if (expectedVersion.HasValue && expectedVersion.Value != config.Version) throw new InvalidOperationException("X settings or search results changed. Reload this page before searching again.");
            if (!config.Configured) throw new InvalidOperationException("Save an X bearer token before searching.");
            if (nextPage && config.NextToken.Length == 0) throw new InvalidOperationException("There is no next page. Start a new search.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var url = "https://api.x.com/2/tweets/search/recent?max_results=10&tweet.fields=lang,edit_history_tweet_ids&query=" + Uri.EscapeDataString(config.Query);
                if (nextPage) url += "&next_token=" + Uri.EscapeDataString(config.NextToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.BearerToken);
                using var response = await clients.CreateClient("X").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) throw new XSearchFailure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "X rejected the bearer token. Check or replace it in settings.",
                    HttpStatusCode.PaymentRequired => "X requires available API credits. Check your X billing and spending limit.",
                    HttpStatusCode.Forbidden => "X denied search access. Check app permissions, API access and billing in the X console.",
                    HttpStatusCode.TooManyRequests => "X rate limit reached. Wait before manually trying again; no automatic retry was made.",
                    HttpStatusCode.BadRequest => "X rejected the query or page token. Check query syntax or start a new search.",
                    _ => "X search is unavailable. Retry manually later; no automatic retry was made."
                });
                var payload = await ReadBoundedAsync(response.Content, timeout.Token);
                var page = Parse(payload); // Validate the entire response before retaining any content.
                var retained = 0; var existing = 0; var skipped = 0;
                await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
                foreach (var post in page.Posts)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (post.Text.Trim().Length is < 20 or > 6000) { skipped++; continue; }
                    var externalId = "x:" + post.CanonicalId;
                    // Edits share the earliest edit-history ID. Preserve already retained original wording.
                    if (await db.InboxMessages.AnyAsync(m => m.OwnerId == owner && m.Channel == "Social media" && m.ExternalId == externalId && m.CapturedBy == Actor, timeout.Token))
                    { existing++; continue; }
                    await inbox.CaptureAsync(owner, Actor, post.Text, "Social media", post.Language == "kn" ? "Kannada" : "English",
                        "https://x.com/i/web/status/" + post.Id, externalId, true, timeout.Token);
                    retained++;
                }
                await transaction.CommitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                var status = $"Search completed: {page.Posts.Count} returned, {retained} retained for review, {existing} already retained, {skipped} outside supported text limits. No AI processing or case registration performed.";
                store.Update(owner, current => current with { Version = Guid.NewGuid(), NextToken = page.NextToken, LastCheckedUtc = clock.GetUtcNow(), LastSuccessUtc = clock.GetUtcNow(), Status = status });
                return new(page.Posts.Count, retained, existing, skipped, page.NextToken.Length > 0, status);
            }
            catch (OperationCanceledException)
            {
                var message = cancellation.IsCancellationRequested ? "X search was cancelled. Pagination was not advanced; retained posts can be safely checked again." : "X search timed out. Pagination was not advanced. Retry manually later.";
                SaveFailure(owner, message);
                if (cancellation.IsCancellationRequested) throw;
                throw new InvalidOperationException(message);
            }
            catch (Exception ex) when (ex is XSearchFailure or HttpRequestException or JsonException or IOException or UnauthorizedAccessException or DbUpdateException)
            {
                var message = ex is XSearchFailure known ? known.Message : "X search could not be completed. Check connectivity and local storage, then retry manually. Pagination was not advanced.";
                SaveFailure(owner, message);
                throw new InvalidOperationException(message); // Never include response bodies, credentials or inner exceptions.
            }
        }
        finally { gate.Release(); }
    }

    private void SaveFailure(Guid owner, string message)
    {
        try { store.Update(owner, current => current with { LastCheckedUtc = clock.GetUtcNow(), Status = message }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Original useful error is returned even if status cannot be saved. */ }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellation)
    {
        const int limit = 200_000;
        if (content.Headers.ContentLength > limit) throw new XSearchFailure("X returned an oversized response. No posts were imported.");
        await using var stream = await content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation)) > 0)
        {
            if (buffer.Length + read > limit) throw new XSearchFailure("X returned an oversized response. No posts were imported.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record Post(string Id, string CanonicalId, string Text, string Language);
    private sealed record Page(List<Post> Posts, string NextToken);
    private sealed class XSearchFailure(string message) : Exception(message);
    private static Page Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        static XSearchFailure Invalid() => new("X returned an incomplete or invalid response. No posts were imported; pagination was not advanced.");
        static bool IsId(string? id) => id is { Length: > 0 and <= 25 } && id.All(c => c is >= '0' and <= '9');
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _)) throw Invalid();
        if (!root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("result_count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var resultCount) || resultCount is < 0 or > 10) throw Invalid();
        var next = "";
        if (meta.TryGetProperty("next_token", out var cursor))
        {
            if (cursor.ValueKind != JsonValueKind.String) throw Invalid();
            next = cursor.GetString()!;
            if (next.Length is < 1 or > 2048 || next.Any(char.IsControl)) throw Invalid();
        }
        var posts = new List<Post>();
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 10) throw Invalid();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String) throw Invalid();
                var id = idValue.GetString()!; var text = textValue.GetString()!;
                if (!IsId(id)) throw Invalid();
                var canonical = id;
                if (item.TryGetProperty("edit_history_tweet_ids", out var history))
                {
                    if (history.ValueKind != JsonValueKind.Array || history.GetArrayLength() is < 1 or > 100) throw Invalid();
                    var ids = history.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).ToArray();
                    if (ids.Any(value => !IsId(value)) || !ids.Contains(id)) throw Invalid();
                    canonical = ids[0]!;
                }
                var language = "";
                if (item.TryGetProperty("lang", out var lang))
                {
                    if (lang.ValueKind != JsonValueKind.String) throw Invalid();
                    language = lang.GetString()!;
                }
                posts.Add(new(id, canonical, text, language));
            }
        }
        if (posts.Count != resultCount) throw Invalid();
        return new(posts, next);
    }
}
