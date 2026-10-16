namespace Praja.Web.Data;
public class AccountAudit
{
    public long Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid TargetId { get; set; }
    public string Action { get; set; } = "";
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}
