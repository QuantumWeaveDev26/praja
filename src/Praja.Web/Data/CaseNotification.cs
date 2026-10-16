using System.ComponentModel.DataAnnotations;
namespace Praja.Web.Data;

public class CaseNotification
{
    public long Id { get; set; }
    public Guid CaseId { get; set; }
    [MaxLength(40)] public string Recipient { get; set; } = "";
    [MaxLength(150)] public string EventKey { get; set; } = "";
    [MaxLength(300)] public string Message { get; set; } = "";
    [MaxLength(20)] public string Severity { get; set; } = "Update";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadUtc { get; set; }
}
