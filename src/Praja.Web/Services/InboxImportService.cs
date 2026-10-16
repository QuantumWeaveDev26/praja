using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using Praja.Web.Data;
using Praja.Web.Domain;

namespace Praja.Web.Services;

public sealed record ImportPreviewRow(int Number, InboxMessage Message, RoutingSuggestion Route,
    Guid? ExistingId, bool RepeatedSource, int BatchMatches, IReadOnlyList<InboxDuplicate> Matches);
public sealed record ImportPreview(string Token, IReadOnlyList<ImportPreviewRow> Rows);
public sealed record ImportedMessage(InboxMessage Message, bool Created);

public sealed class InboxImportService(WorkspaceDb db, InboxService inbox, IDataProtectionProvider protection, TimeProvider clock)
{
    public const int MaximumCharacters = 32_768;
    public const int MaximumRows = 20;
    private const string Header = "channel,external_id,language,source_url,message";
    private readonly IDataProtector protector = protection.CreateProtector("Praja.InboxImport.v1");
    private sealed record Receipt(Guid Owner, DateTimeOffset Expires, string Csv);

    public static string SampleCsv => Header + "\n"
        + "Email,praja-demo-email-001,English,,\"[Fictional sample] No water supply in Ward 12, Mysuru, for three days. Please restore supply.\"\n"
        + "Field meeting,praja-demo-field-001,English,,\"[Fictional sample] Uncollected garbage is blocking the street in Ward 12, Mysuru. Please clear it.\"\n"
        + "Social media,praja-demo-social-001,Kannada,https://social.example.test/posts/praja-demo-001,[Fictional sample] ಶಾಲೆಯಲ್ಲಿ ಶಿಕ್ಷಕರಿಲ್ಲ. ದಯವಿಟ್ಟು ಶಿಕ್ಷಕರನ್ನು ನೇಮಿಸಿ.\n";

    // Exact fixture recognition keeps demo cases out of normal analytics without a schema change.
    private static readonly IReadOnlyList<InboxMessage> SampleMessages = Parse(SampleCsv, Guid.Empty, "Demo");
    public static bool IsDemoSample(InboxMessage item) => SampleMessages
        .Any(sample => sample.Channel == item.Channel && sample.ExternalId == item.ExternalId
            && sample.SourceUrl == item.SourceUrl && sample.Language == item.Language && sample.OriginalText == item.OriginalText);

    public async Task<ImportPreview> PreviewAsync(Guid owner, string actor, string csv, CancellationToken cancellation = default)
    {
        var messages = Parse(csv, owner, actor);
        var rows = new List<ImportPreviewRow>();
        var seen = new Dictionary<string, string>();
        foreach (var message in messages)
        {
            var key = message.IdempotencyKey!;
            var repeated = seen.TryGetValue(key, out var hash);
            if (repeated && hash != message.Fingerprint) throw new InvalidOperationException("One source message ID occurs with different wording in this batch. Correct the CSV before importing.");
            seen[key] = message.Fingerprint;
            var prior = await db.InboxMessages.AsNoTracking().SingleOrDefaultAsync(m => m.OwnerId == owner && m.IdempotencyKey == key, cancellation);
            if (prior is not null && prior.Fingerprint != message.Fingerprint) throw new InvalidOperationException("This source message ID was already retained with different wording. Review the existing inbox item; no messages were imported.");
            message.GrievanceId = prior?.GrievanceId;
            var matches = (await inbox.DuplicatesAsync(message, cancellation)).Where(m => m.Id != prior?.Id).ToList();
            var batchMatches = messages.Count(m => m.Id != message.Id && m.Fingerprint == message.Fingerprint && m.IdempotencyKey != key);
            rows.Add(new(rows.Count + 1, message, CaseRules.SuggestDepartment(message.OriginalText), prior?.Id, repeated, batchMatches, matches));
        }
        var token = protector.Protect(JsonSerializer.Serialize(new Receipt(owner, clock.GetUtcNow().AddMinutes(30), csv),
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return new(token, rows);
    }

    public string ReadPreviewCsv(string token, Guid owner) => Read(token, owner);

    public async Task<IReadOnlyList<ImportedMessage>> ImportAsync(Guid owner, string actor, string token, bool permission, CancellationToken cancellation = default)
    {
        if (!permission) throw new InvalidOperationException("Confirm permission to retain these messages and their source details.");
        var messages = Parse(Read(token, owner), owner, actor);
        // A preview may have become stale. Recheck every identity inside the transaction;
        // a conflict, failed insert or cancellation rolls back the entire batch.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
        try
        {
            var results = new List<ImportedMessage>();
            foreach (var message in messages)
            {
                var saved = await inbox.CapturePreparedAsync(message, cancellation);
                results.Add(new(saved, saved.Id == message.Id));
            }
            await transaction.CommitAsync(cancellation);
            return results;
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private string Read(string token, Guid owner)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 200_000) throw new InvalidOperationException();
            var receipt = JsonSerializer.Deserialize<Receipt>(protector.Unprotect(token));
            if (receipt is null || receipt.Owner != owner || receipt.Expires <= clock.GetUtcNow()) throw new InvalidOperationException();
            return receipt.Csv;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException or ArgumentException or FormatException)
        { throw new InvalidOperationException("Preview these messages again. The preview expired, changed or belongs to another officer."); }
    }

    private static List<InboxMessage> Parse(string csv, Guid owner, string actor)
    {
        if (string.IsNullOrWhiteSpace(csv) || csv.Length > MaximumCharacters) throw new InvalidOperationException("Paste CSV messages within the 32,768-character limit.");
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        try
        {
            var header = parser.ReadFields();
            if (header is null || !header.Select(h => h.Trim()).SequenceEqual(Header.Split(','), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use the CSV header: " + Header + ".");
            var rows = new List<InboxMessage>();
            var identities = new Dictionary<string, string>();
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields is null) break;
                if (rows.Count >= MaximumRows) throw new InvalidOperationException("Import at most 20 messages at a time. Split this CSV into smaller batches.");
                var number = rows.Count + 1;
                if (fields.Length != 5) throw new InvalidOperationException($"Message {number}: each CSV row needs five fields. Quote messages containing commas or line breaks.");
                var channel = fields[0].Trim(); var id = fields[1].Trim(); var language = fields[2].Trim();
                if (channel is not ("Email" or "Field meeting" or "Social media")) throw new InvalidOperationException($"Message {number}: choose channel Email, Field meeting or Social media.");
                if (id.Length == 0) throw new InvalidOperationException($"Message {number}: include a stable source message ID so retries do not create another item.");
                if (fields[4].Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new InvalidOperationException($"Message {number}: remove unsupported control characters from the text.");
                InboxMessage message;
                try { message = InboxService.Prepare(owner, actor, fields[4], channel, language, fields[3], id); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException($"Message {number}: {ex.Message}"); }
                if (identities.TryGetValue(message.IdempotencyKey!, out var previous) && previous != message.Fingerprint)
                    throw new InvalidOperationException($"Message {number}: the same source message ID has different wording in this batch.");
                identities[message.IdempotencyKey!] = message.Fingerprint;
                rows.Add(message);
            }
            if (rows.Count == 0) throw new InvalidOperationException("Add at least one CSV message below the header.");
            return rows;
        }
        catch (MalformedLineException) { throw new InvalidOperationException("This CSV has an incomplete or invalid quoted field. Check quotes, commas and line breaks."); }
    }
}
