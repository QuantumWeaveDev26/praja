using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Praja.Web.Services;

public sealed partial class GmailService
{
    private sealed class UnsupportedMailException(string message) : Exception(message);
    private const int MaximumPartBytes = 65536;

    private async Task<string?> ReadContentAsync(JsonElement payload, string messageId, string token, CancellationToken cancellation, bool allowPdfOcr = false)
    {
        var parts = new List<JsonElement>();
        void Visit(JsonElement part, int depth)
        {
            if (depth > 8 || parts.Count >= 60) throw new UnsupportedMailException("MIME structure exceeds supported limits.");
            parts.Add(part);
            if (part.TryGetProperty("parts", out var nested)) foreach (var child in nested.EnumerateArray()) Visit(child, depth + 1);
        }
        Visit(payload, 0);
        static string Name(JsonElement p) => p.TryGetProperty("filename", out var n) ? n.GetString() ?? "" : "";
        static string Mime(JsonElement p) => p.TryGetProperty("mimeType", out var m) ? (m.GetString() ?? "").ToLowerInvariant() : "";
        var attachments = parts.Where(p => Name(p).Length > 0).ToList();
        if (attachments.Count > 3) throw new UnsupportedMailException("More than three attachments require manual review.");
        foreach (var attachment in attachments)
        {
            var extension = Path.GetExtension(Name(attachment)).ToLowerInvariant();
            if (extension is not (".txt" or ".pdf")) throw new UnsupportedMailException("Only UTF-8 .txt and PDF attachments are supported.");
        }
        var bodyParts = parts.Where(p => Name(p).Length == 0 && Mime(p) is "text/plain" or "text/html").ToList();
        if (bodyParts.Count(p => Mime(p) == "text/plain") > 1 || (!bodyParts.Any(p => Mime(p) == "text/plain") && bodyParts.Count > 1))
            throw new UnsupportedMailException("Multiple body sections need manual review to avoid omitting content.");
        var selected = bodyParts.FirstOrDefault(p => Mime(p) == "text/plain");
        if (selected.ValueKind == JsonValueKind.Undefined) selected = bodyParts.FirstOrDefault();
        var text = "";
        if (selected.ValueKind != JsonValueKind.Undefined)
        {
            EnsureUtf8(selected);
            text = Decode(await PartBytes(selected, messageId, token, cancellation));
            if (Mime(selected) == "text/html") text = HtmlText(text);
        }
        // Download all attachments and check byte limits before OCR. Never import a partial email.
        var downloaded = new List<(JsonElement Part, byte[] Bytes)>();
        var totalBytes = 0;
        foreach (var attachment in attachments)
        {
            var limit = allowPdfOcr && Path.GetExtension(Name(attachment)).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? 8 * 1024 * 1024 : MaximumPartBytes;
            var bytes = await PartBytes(attachment, messageId, token, cancellation, Math.Min(limit, 8 * 1024 * 1024 - totalBytes));
            totalBytes += bytes.Length;
            downloaded.Add((attachment, bytes));
        }
        foreach (var (attachment, bytes) in downloaded)
        {
            var extension = Path.GetExtension(Name(attachment)).ToLowerInvariant();
            string extracted;
            var method = "local " + extension + " text";
            if (extension == ".txt") { EnsureUtf8(attachment); extracted = Decode(bytes).TrimStart('\uFEFF'); }
            else
            {
                if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new UnsupportedMailException("Attachment is not a recognised PDF.");
                PdfInspection inspection;
                try { inspection = await PdfIntakeWorker.InspectAsync(bytes, cancellation); }
                catch (InvalidOperationException) { throw new UnsupportedMailException("PDF inspection failed; no partial text extracted."); }
                if (inspection.RequiresOcr)
                {
                    if (!allowPdfOcr) throw new UnsupportedMailException("Scanned PDFs need automatic intake with the configured PDF OCR service.");
                    if (media is null) throw new InvalidOperationException("PDF OCR service is unavailable. Retry after configuring AI.");
                    // Provider failures remain retryable: never mark this message unsupported or seen.
                    var result = await media.ReadScannedPdfAsync(bytes, inspection.PageCount, cancellation);
                    extracted = result.Text;
                    method = "PDF OCR; verify against original";
                }
                else extracted = inspection.Text;
            }
            if (string.IsNullOrWhiteSpace(extracted)) throw new UnsupportedMailException("An attachment has no readable text.");
            // Use a fixed ordinal rather than an untrusted filename in complaint text.
            text += $"\n\n[Attachment {attachments.IndexOf(attachment) + 1}: {method}]\n" + extracted;
            if (text.Length > 6000) throw new UnsupportedMailException("Combined text exceeds 6,000 characters; no truncation is performed.");
        }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task<byte[]> PartBytes(JsonElement part, string messageId, string token, CancellationToken cancellation, int maximumBytes = MaximumPartBytes)
    {
        if (!part.TryGetProperty("body", out var body)) throw new UnsupportedMailException("Missing MIME content.");
        if (body.TryGetProperty("size", out var size) && (!size.TryGetInt32(out var count) || count < 0 || count > maximumBytes))
            throw new UnsupportedMailException("A body or attachment exceeds its byte limit (text: 64 KiB; automatic PDFs: 8 MiB total).");
        string encoded;
        if (body.TryGetProperty("data", out var data)) encoded = data.GetString() ?? "";
        else if (body.TryGetProperty("attachmentId", out var attachment))
        {
            var id = attachment.GetString() ?? "";
            if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,2048}$")) throw new UnsupportedMailException("Unsupported attachment identifier.");
            // Transient request failures propagate, so collection can retry without marking the message seen.
            JsonDocument reply;
            try { reply = await Get("messages/" + messageId + "/attachments/" + id, token, cancellation); }
            catch (OversizedGmailContentException) { throw new UnsupportedMailException("Attachment response exceeds supported limits."); }
            using var replyLifetime = reply;
            if (!reply.RootElement.TryGetProperty("data", out var content)) throw new UnsupportedMailException("Missing attachment content.");
            encoded = content.GetString() ?? "";
        }
        else return [];
        if (encoded.Length > ((long)maximumBytes + 2) / 3 * 4) throw new UnsupportedMailException("A body or attachment exceeds its byte limit (text: 64 KiB; automatic PDFs: 8 MiB total).");
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(encoded);
            if (bytes.Length > maximumBytes) throw new UnsupportedMailException("A body or attachment exceeds its byte limit (text: 64 KiB; automatic PDFs: 8 MiB total).");
            return bytes;
        }
        catch (FormatException) { throw new UnsupportedMailException("Invalid attachment or body encoding."); }
    }

    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new UnsupportedMailException("Content is not valid UTF-8."); }
    }
    private static void EnsureUtf8(JsonElement part)
    {
        if (!part.TryGetProperty("headers", out var headers)) return;
        foreach (var header in headers.EnumerateArray())
        {
            if (!string.Equals(header.GetProperty("name").GetString(), "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            var charset = Regex.Match(header.GetProperty("value").GetString() ?? "", @"charset\s*=\s*[""']?([^;\s""']+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)).Groups[1].Value;
            if (charset.Length > 0 && charset.ToLowerInvariant() is not ("utf-8" or "utf8" or "us-ascii")) throw new UnsupportedMailException("Unsupported text character set.");
        }
    }
    private static string HtmlText(string html)
    {
        // This produces plain text, never HTML for rendering. URLs/resources are never loaded.
        string Replace(string value, string pattern, string replacement) => Regex.Replace(value, pattern, replacement,
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        try
        {
            html = Replace(html, @"<!--.*?(?:-->|$)", " ");
            html = Replace(html, @"<(script|style|head|iframe|object|svg)\b[^>]*>.*?(?:</\1\s*>|$)", " ");
            html = Replace(html, @"</?(?:p|div|br|li|tr|h[1-6]|section)\b[^>]*>", "\n");
            html = Replace(html, @"<[^>]*>", " ");
            return WebUtility.HtmlDecode(html).Trim();
        }
        catch (RegexMatchTimeoutException) { throw new UnsupportedMailException("HTML is too complex to extract safely."); }
    }
}
