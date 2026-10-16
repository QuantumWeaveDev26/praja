using System.ComponentModel.DataAnnotations;

namespace Praja.Web.Data;

public class CommunityVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    [MaxLength(64)] public string BrowserHash { get; set; } = "";
    [MaxLength(20)] public string Decision { get; set; } = "";
    [MaxLength(500)] public string Note { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(20)] public string ReviewState { get; set; } = "Pending";
    [MaxLength(1000)] public string ReviewNote { get; set; } = "";
    public Guid? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public int Version { get; set; } = 1;
    public int FeedbackRound { get; set; } = 1;
}
