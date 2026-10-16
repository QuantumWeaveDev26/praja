using Microsoft.AspNetCore.DataProtection;
using Praja.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Praja.Web.Services;

public record EvidenceItem(Guid Id, string FileName, string Description, int ByteLength, string Sha256, DateTime CreatedUtc, string UploadedBy);
public record EvidenceDownload(string FileName, byte[] Bytes);
public class EvidenceService(WorkspaceDb db, IDataProtectionProvider protection)
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    public async Task<EvidenceItem> UploadAsync(Guid caseId, Guid owner, string actor, int version, IFormFile file, string? description, bool consent, CancellationToken cancellation = default)
    {
        var item = await db.Grievances.SingleOrDefaultAsync(g => g.Id == caseId && g.OwnerId == owner, cancellation) ?? throw new KeyNotFoundException();
        if (!consent) throw new InvalidOperationException("Confirm permission to retain this supporting file.");
        if (item.Version != version) throw new InvalidOperationException("This case changed. Reload it before uploading evidence.");
        if (item.Status is not ("Registered" or "InProgress" or "ClosureReview")) throw new InvalidOperationException("Upload evidence after registration and before closure.");
        if (description?.Length > 500 || actor.Length > 100) throw new InvalidOperationException("Keep the evidence description within 500 characters.");
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidOperationException("Upload a non-empty PDF, PNG or JPEG of at most 8 MB.");
        if (await db.EvidenceAttachments.CountAsync(e => e.GrievanceId == caseId, cancellation) >= 10) throw new InvalidOperationException("This case already has the maximum of 10 supporting files.");
        var name = SafeName(file.FileName);
        await using var stream = file.OpenReadStream(); using var memory = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer,cancellation)) > 0)
        {
            if (memory.Length + read > MaximumBytes) throw new InvalidOperationException("The supporting file exceeds 8 MB.");
            await memory.WriteAsync(buffer.AsMemory(0,read),cancellation);
        }
        var bytes = memory.ToArray();
        var mime = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf" when bytes.AsSpan().StartsWith("%PDF-"u8) => "application/pdf",
            ".png" when bytes.AsSpan().StartsWith(new byte[] {137,80,78,71,13,10,26,10}) => "image/png",
            ".jpg" or ".jpeg" when bytes.AsSpan().StartsWith(new byte[] {255,216,255}) => "image/jpeg",
            _ => throw new InvalidOperationException("The file extension and signature must identify a PDF, PNG or JPEG.")
        };
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (await db.EvidenceAttachments.AnyAsync(e => e.GrievanceId == caseId && e.Sha256 == hash,cancellation)) throw new InvalidOperationException("This file is already attached to the case.");
        var attachment = new EvidenceAttachment { GrievanceId=caseId, FileName=name, ContentType=mime, Description=description?.Trim() ?? "", ByteLength=bytes.Length, Sha256=hash, UploadedBy=actor };
        attachment.ProtectedContent = Protector(owner,caseId,attachment.Id).Protect(bytes);
        db.EvidenceAttachments.Add(attachment);
        item.Version++; item.UpdatedUtc=DateTime.UtcNow;
        item.History.Add(new() {Actor=actor,Action=$"Supporting file retained: {name} ({bytes.Length} bytes). SHA-256: {hash}. Authenticity and resolution are not independently verified."});
        try { await db.SaveChangesAsync(cancellation); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); throw new InvalidOperationException("This case changed during upload. Reload it; no supporting file was saved.");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); throw new InvalidOperationException("The supporting file could not be saved. Reload the case and retry.");
        }
        return Metadata(attachment);
    }
    public async Task<IReadOnlyList<EvidenceItem>> ListAsync(Guid caseId, Guid owner, CancellationToken cancellation = default)
    {
        if (!await db.Grievances.AnyAsync(g=>g.Id==caseId && g.OwnerId==owner,cancellation)) return [];
        return await db.EvidenceAttachments.AsNoTracking().Where(e=>e.GrievanceId==caseId).OrderByDescending(e=>e.CreatedUtc).Take(10)
            .Select(e=>new EvidenceItem(e.Id,e.FileName,e.Description,e.ByteLength,e.Sha256,e.CreatedUtc,e.UploadedBy)).ToListAsync(cancellation);
    }
    public async Task<EvidenceDownload?> DownloadAsync(Guid caseId, Guid attachmentId, Guid owner, CancellationToken cancellation = default)
    {
        if (!await db.Grievances.AnyAsync(g=>g.Id==caseId && g.OwnerId==owner,cancellation)) return null;
        var item=await db.EvidenceAttachments.AsNoTracking().SingleOrDefaultAsync(e=>e.Id==attachmentId && e.GrievanceId==caseId,cancellation);
        if(item is null) return null;
        try
        {
            if(item.ProtectedContent.Length>MaximumBytes+1024 || item.ByteLength is <=0 or >MaximumBytes) throw new CryptographicException();
            var bytes=Protector(owner,caseId,item.Id).Unprotect(item.ProtectedContent);
            if(bytes.Length!=item.ByteLength || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),Convert.FromHexString(item.Sha256))) throw new CryptographicException();
            return new(SafeName(item.FileName),bytes);
        }
        catch(Exception ex) when(ex is CryptographicException or FormatException)
        { throw new InvalidOperationException("This supporting file is unavailable or failed its integrity check. Restore a verified backup; no file content was returned."); }
    }
    private IDataProtector Protector(Guid owner,Guid caseId,Guid attachmentId)
    {
        var connection=db.Database.GetDbConnection();
        var identity=(db.Database.ProviderName ?? "")+"|"+connection.DataSource+"|"+connection.Database;
        var scope=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return protection.CreateProtector("Praja.SupportingEvidence.v1",scope,owner.ToString("N"),caseId.ToString("N"),attachmentId.ToString("N"));
    }
    private static string SafeName(string raw)
    {
        var name=raw.Replace('\\','/').Split('/').Last().Trim();
        if(name.Length is <1 or >160 || name.Any(c=>char.IsControl(c))) throw new InvalidOperationException("Use a filename of 1–160 characters without control characters.");
        return name;
    }
    private static EvidenceItem Metadata(EvidenceAttachment e)=>new(e.Id,e.FileName,e.Description,e.ByteLength,e.Sha256,e.CreatedUtc,e.UploadedBy);
}
