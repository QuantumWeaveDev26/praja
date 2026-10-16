using System.ComponentModel.DataAnnotations;

namespace Praja.Web.Data;
public class InboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    [MaxLength(6000)] public string OriginalText { get; set; } = "";
    [MaxLength(20)] public string Channel { get; set; } = "Text";
    [MaxLength(20)] public string Language { get; set; } = "English";
    [MaxLength(1000)] public string SourceUrl { get; set; } = "";
    [MaxLength(150)] public string? ExternalId { get; set; }
    [MaxLength(64)] public string? IdempotencyKey {get;set;}
    [MaxLength(64)] public string Fingerprint { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(120)] public string CapturedBy { get; set; } = "";
    [MaxLength(20)] public string State { get; set; } = "Pending";
    public int Version { get; set; } = 1;
    public Guid? GrievanceId { get; set; }
    public Guid? DuplicateOfId { get; set; }
    public Grievance? Grievance { get; set; }
    [MaxLength(500)] public string ReviewNote { get; set; } = "";
    [MaxLength(120)] public string ReviewedBy { get; set; } = "";
    public DateTime? ReviewedUtc { get; set; }
}
