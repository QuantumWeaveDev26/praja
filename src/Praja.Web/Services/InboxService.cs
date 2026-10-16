using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;
public sealed record InboxDuplicate(Guid Id, string Label, bool IsCase, string Reason = "Exact normalized wording");
public partial class InboxService(WorkspaceDb db)
{
    public static readonly string[] Channels = ["Text", "Email", "Field meeting", "Paper transcription", "Document extraction", "Voice transcript", "Social media"];
    public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant())));
    public Task<InboxMessage?> FindAsync(Guid id, Guid owner, CancellationToken cancellation = default) =>
        db.InboxMessages.SingleOrDefaultAsync(m => m.Id == id && m.OwnerId == owner, cancellation);

    public async Task<InboxMessage> CaptureAsync(Guid owner, string actor, string text, string channel, string language, string? sourceUrl, string? externalId, bool permission, CancellationToken cancellation = default)
    {
        if (!permission) throw new InvalidOperationException("Confirm permission to retain this message and source details.");
        return await CapturePreparedAsync(Prepare(owner, actor, text, channel, language, sourceUrl, externalId), cancellation);
    }

    internal static InboxMessage Prepare(Guid owner, string actor, string text, string channel, string language, string? sourceUrl, string? externalId)
    {
        text = text.Trim(); sourceUrl = sourceUrl?.Trim() ?? ""; externalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();
        if (text.Length is < 20 or > 6000 || !Channels.Contains(channel) || language is not ("English" or "Kannada"))
            throw new InvalidOperationException("Enter a message of 20–6,000 characters and choose a supported channel and language.");
        if (sourceUrl.Length > 1000 || externalId?.Length > 150 || sourceUrl.Any(char.IsControl) || externalId?.Any(char.IsControl) == true)
            throw new InvalidOperationException("Keep source details within the displayed limits without control characters.");
        if (sourceUrl.Length > 0 && (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var link) || link.Scheme != "https" || link.UserInfo.Length > 0 || string.IsNullOrWhiteSpace(link.Host)))
            throw new InvalidOperationException("Source links must be absolute HTTPS URLs without embedded credentials.");
        if(channel=="Social media"&&externalId is not null&&sourceUrl.Length==0)throw new InvalidOperationException("Include the source link when using a social-post ID so its platform can be identified.");
        var key=externalId is null?null:Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {channel,origin=sourceUrl.Length==0?"manual":new Uri(sourceUrl).IdnHost.ToLowerInvariant(),externalId}))));
        var hash = Fingerprint(text);
        return new InboxMessage { OwnerId = owner, CapturedBy = actor, OriginalText = text, Channel = channel, Language = language, SourceUrl = sourceUrl, ExternalId = externalId, IdempotencyKey=key, Fingerprint = hash };
    }

    internal async Task<InboxMessage> CapturePreparedAsync(InboxMessage item, CancellationToken cancellation = default)
    {
        async Task<InboxMessage?> Existing()
        {
            var prior = item.IdempotencyKey is null ? null : await db.InboxMessages.SingleOrDefaultAsync(m => m.OwnerId == item.OwnerId && m.IdempotencyKey == item.IdempotencyKey, cancellation);
            if (prior is not null && prior.Fingerprint != item.Fingerprint) throw new InvalidOperationException("This source message ID was already retained with different wording. Review the existing item; its original text was preserved.");
            return prior;
        }
        if (await Existing() is { } existing) return existing;
        db.InboxMessages.Add(item);
        try { await db.SaveChangesAsync(cancellation); }
        catch (DbUpdateException) when (item.ExternalId is not null)
        {
            db.ChangeTracker.Clear();
            if (await Existing() is { } raced) return raced;
            throw;
        }
        return item;
    }

    public async Task<List<InboxDuplicate>> DuplicatesAsync(InboxMessage item, CancellationToken cancellation = default)
    {
        var recentInbox = await db.InboxMessages.AsNoTracking().Where(m => m.OwnerId == item.OwnerId && m.Id != item.Id && m.State == "Pending")
            .OrderByDescending(m => m.ReceivedUtc).Take(200).ToListAsync(cancellation);
        var other = recentInbox.Select(m => new { Item=m, Reason=MatchReason(item.OriginalText,m.OriginalText) }).Where(m=>m.Reason is not null)
            .Take(10).Select(m=>new InboxDuplicate(m.Item.Id,m.Item.Channel+" · "+m.Item.State,false,m.Reason!)).ToList();
        var recent = await db.Grievances.AsNoTracking().Where(g => g.OwnerId == item.OwnerId && g.Id != item.GrievanceId && !g.IsSample)
            .OrderByDescending(g => g.CreatedUtc).Take(200).ToListAsync(cancellation);
        other.AddRange(recent.Select(g=>new {Item=g,Reason=MatchReason(item.OriginalText,g.OriginalText)}).Where(g=>g.Reason is not null)
            .Take(10).Select(g=>new InboxDuplicate(g.Item.Id,g.Item.Reference+" · "+g.Item.Status,true,g.Reason!)));
        return other;
    }

    public async Task DismissAsync(Guid id, Guid owner, int version, string note, string actor, CancellationToken cancellation = default)
    {
        var item = await FindAsync(id, owner, cancellation) ?? throw new InvalidOperationException("Inbox item not found.");
        if (item.State != "Pending" || item.Version != version) throw new InvalidOperationException("This inbox item changed. Reload before reviewing it.");
        if (note.Trim().Length is < 20 or > 500) throw new InvalidOperationException("Record a review reason of 20–500 characters.");
        item.State = "Dismissed"; item.ReviewNote = note.Trim(); item.ReviewedBy = actor; item.ReviewedUtc = DateTime.UtcNow; item.Version++;
        try { await db.SaveChangesAsync(cancellation); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); throw new InvalidOperationException("This inbox item changed. Reload before reviewing it."); }
    }
}
