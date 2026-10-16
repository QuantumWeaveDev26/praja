using System.ComponentModel.DataAnnotations;

namespace Praja.Web.Data;

public class EvidenceAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GrievanceId { get; set; }
    [MaxLength(160)] public string FileName { get; set; } = "";
    [MaxLength(100)] public string ContentType { get; set; } = "";
    [MaxLength(500)] public string Description { get; set; } = "";
    [MaxLength(64)] public string Sha256 { get; set; } = "";
    public int ByteLength { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(100)] public string UploadedBy { get; set; } = "";
    public byte[] ProtectedContent { get; set; } = [];
}
