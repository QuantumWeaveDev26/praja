using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Praja.Web.Domain;

namespace Praja.Web.Services;

public sealed record IntakeAnalysis(string District, string Location, string Timeline, string RequestedAction,
    string Language, RoutingSuggestion Route, string Method, bool RejectedUnsupportedFields);

// Review receipts protect the provenance of suggestions without writing an unconfirmed case.
public sealed class IntakeReviewService(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Praja.IntakeReview.v1");
    private sealed record Receipt(Guid Owner, string SourceHash, DateTimeOffset Expires, IntakeAnalysis Analysis);
    private static string Hash(string original) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original?.Trim() ?? "")));

    public string Protect(Guid owner, string original, IntakeAnalysis analysis) =>
        protector.Protect(JsonSerializer.Serialize(new Receipt(owner, Hash(original), clock.GetUtcNow().AddMinutes(30), analysis)));

    public IntakeAnalysis Read(string token, Guid owner, string original)
    {
        try
        {
            if (token.Length > 16_000) throw new InvalidOperationException();
            var receipt = JsonSerializer.Deserialize<Receipt>(protector.Unprotect(token));
            if (receipt is null || receipt.Owner != owner || receipt.SourceHash != Hash(original) || receipt.Expires <= clock.GetUtcNow())
                throw new InvalidOperationException();
            return receipt.Analysis;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("These suggestions expired or the original complaint changed. Select Suggest details again, or clear the suggestions and enter details manually.");
        }
    }
}
