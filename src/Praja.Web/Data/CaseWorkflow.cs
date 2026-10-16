using System.ComponentModel.DataAnnotations;
namespace Praja.Web.Data;

public class CitizenReceipt
{
    public Guid Id { get; set; }
    public Guid SubmissionKey { get; set; }
    public Guid AccessVersion { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class OfficerRole
{
    public Guid Id { get; set; }
    [MaxLength(30)] public string Role { get; set; } = "Intake";
    [MaxLength(30)] public string Department { get; set; } = "";
}
public class CaseWorkflow
{
    public Guid Id { get; set; }
    public Guid AssignedOfficerId { get; set; }
    public Guid? ContractorId { get; set; }
    public bool ContractorAccepted { get; set; }
    public DateTime DueUtc { get; set; }
    [MaxLength(30)] public string Stage { get; set; } = "Assigned";
    public int Version { get; set; } = 1;
    public Guid AccessVersion { get; set; } = Guid.NewGuid();
    public Guid? CompletionPhotoId { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime? CapturedUtc { get; set; }
    [MaxLength(3000)] public string CompletionNote { get; set; } = "";
    public Guid? CompletedBy { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTime? ReopenedUtc { get; set; }
    public int FeedbackRound { get; set; } = 1;
}
public class WorkflowUpdate
{
    public int Id { get; set; }
    public Guid CaseId { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(100)] public string Actor { get; set; } = "";
    [MaxLength(30)] public string Stage { get; set; } = "";
    [MaxLength(1500)] public string Note { get; set; } = "";
}
