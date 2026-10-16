using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;
using System.Security.Cryptography;
using System.Text.Json;

namespace Praja.Web.Services;

public record CitizenAccessCode(string Code, DateTimeOffset ExpiresUtc);
public record CitizenCaseStatus(string Reference, string Title, string Status, DateTime UpdatedUtc, string District, string Service);

public class CitizenAccessService(WorkspaceDb db, IDataProtectionProvider protection, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);
    private readonly IDataProtector protector = protection.CreateProtector("Praja.CitizenCaseAccess.v1");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CitizenAccessCode?> IssueAsync(Guid caseId, Guid ownerId, TimeSpan? lifetime = null, CancellationToken cancellation = default)
    {
        var duration = lifetime ?? DefaultLifetime;
        if (duration <= TimeSpan.Zero || duration > MaximumLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (caseId == Guid.Empty || ownerId == Guid.Empty || !await db.Grievances.AsNoTracking()
            .AnyAsync(g => g.Id == caseId && g.OwnerId == ownerId && g.Status != "Draft", cancellation)) return null;
        var now = clock.GetUtcNow();
        var expires = now.Add(duration);
        var payload = new AccessPayload(caseId, ownerId, now, expires, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        return new(protector.Protect(JsonSerializer.Serialize(payload)), expires);
    }

    public async Task<CitizenCaseStatus?> GetStatusAsync(string? code, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 2048) return null;
        AccessPayload? payload;
        try { payload = JsonSerializer.Deserialize<AccessPayload>(protector.Unprotect(code.Trim())); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException) { return null; }
        var now = clock.GetUtcNow();
        if (payload is null || payload.CaseId == Guid.Empty || payload.OwnerId == Guid.Empty || payload.Nonce?.Length != 64 ||
            payload.IssuedUtc > now || payload.ExpiresUtc <= now || payload.ExpiresUtc <= payload.IssuedUtc ||
            payload.ExpiresUtc - payload.IssuedUtc > MaximumLifetime) return null;
        var result = await db.Grievances.AsNoTracking()
            .Where(g => g.Id == payload.CaseId && g.OwnerId == payload.OwnerId && g.Status != "Draft")
            .Select(g => new CitizenCaseStatus(g.Reference, g.Title, g.Status, g.UpdatedUtc, g.District, g.Department))
            .SingleOrDefaultAsync(cancellation);
        return result is null ? null : result with { Service = CaseRules.Departments.GetValueOrDefault(result.Service, "Manual routing review") };
    }

    private sealed record AccessPayload(Guid CaseId, Guid OwnerId, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, string Nonce);
}
