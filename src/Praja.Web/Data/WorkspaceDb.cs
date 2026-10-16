using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Praja.Web.Data;

public class Operator
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BootstrapSlot { get; set; } = 1;
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(200)] public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public bool IsDisabled { get; set; }
    public int AccountVersion { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
}

public class Grievance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    [MaxLength(30)] public string Reference { get; set; } = "";
    [MaxLength(180)] public string Title { get; set; } = "";
    [MaxLength(6000)] public string OriginalText { get; set; } = "";
    [MaxLength(8000)] public string DraftText { get; set; } = "";
    [MaxLength(2000)] public string Summary { get; set; } = "";
    [MaxLength(100)] public string District { get; set; } = "";
    [MaxLength(200)] public string Location { get; set; } = "";
    public double? IncidentLatitude { get; set; }
    public double? IncidentLongitude { get; set; }
    public double? IncidentAccuracyMetres { get; set; }
    public Guid? IncidentPhotoId { get; set; }
    public bool PublicMapConsent { get; set; }
    [MaxLength(20)] public string Language { get; set; } = "English";
    [MaxLength(20)] public string Source { get; set; } = "Text";
    [MaxLength(30)] public string Department { get; set; } = "review";
    [MaxLength(30)] public string Status { get; set; } = "Draft";
    [MaxLength(500)] public string RoutingReason { get; set; } = "";
    [MaxLength(40)] public string CategoryCode { get; set; } = "";
    [MaxLength(40)] public string SubcategoryCode { get; set; } = "";
    [MaxLength(250)] public string RoutingEvidence { get; set; } = "";
    [MaxLength(120)] public string RoutingMethod { get; set; } = "";
    [MaxLength(200)] public string DraftMethod { get; set; } = "Structured template";
    public bool IsSample { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }
    public int Version { get; set; } = 1;
    [MaxLength(6000)] public string ResolutionReport { get; set; } = "";
    [MaxLength(500)] public string EvidenceReference { get; set; } = "";
    [MaxLength(20)] public string CitizenFeedback { get; set; } = "Pending";
    public List<AuditEntry> History { get; set; } = [];
}

public class AuditEntry
{
    public int Id { get; set; }
    public Guid GrievanceId { get; set; }
    [MaxLength(120)] public string Actor { get; set; } = "";
    [MaxLength(500)] public string Action { get; set; } = "";
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public class WorkspaceDb(DbContextOptions<WorkspaceDb> options) : DbContext(options)
{
    public DbSet<CommunityVerification> CommunityVerifications => Set<CommunityVerification>();
    public DbSet<AccountAudit> AccountAudits => Set<AccountAudit>();
    public DbSet<CaseNotification> CaseNotifications => Set<CaseNotification>();
    public DbSet<OfficerRole> OfficerRoles => Set<OfficerRole>();
    public DbSet<CitizenReceipt> CitizenReceipts => Set<CitizenReceipt>();
    public DbSet<CaseWorkflow> CaseWorkflows => Set<CaseWorkflow>();
    public DbSet<WorkflowUpdate> WorkflowUpdates => Set<WorkflowUpdate>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<Grievance> Grievances => Set<Grievance>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<EvidenceAttachment> EvidenceAttachments => Set<EvidenceAttachment>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CommunityVerification>().HasOne<Grievance>().WithMany().HasForeignKey(r => r.CaseId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CommunityVerification>().HasIndex(r => new { r.CaseId, r.BrowserHash, r.FeedbackRound }).IsUnique();
        model.Entity<CommunityVerification>().Property(r => r.FeedbackRound).HasDefaultValue(1);
        model.Entity<CommunityVerification>().Property(r => r.Version).IsConcurrencyToken();
        model.Entity<CommunityVerification>().Property(r => r.ReviewState).HasDefaultValue("Pending");
        model.Entity<CommunityVerification>().Property(r => r.Version).HasDefaultValue(1);
        model.Entity<CaseNotification>().HasOne<Grievance>().WithMany().HasForeignKey(n=>n.CaseId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CaseNotification>().HasIndex(n=>new {n.CaseId,n.Recipient,n.EventKey}).IsUnique();
        model.Entity<CaseNotification>().HasIndex(n=>new {n.Recipient,n.ReadUtc});
        model.Entity<OfficerRole>().HasOne<Operator>().WithMany().HasForeignKey(r=>r.Id).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CitizenReceipt>().HasOne<Grievance>().WithMany().HasForeignKey(r=>r.Id).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CitizenReceipt>().HasIndex(r=>r.SubmissionKey).IsUnique();
        model.Entity<CaseWorkflow>().HasOne<Grievance>().WithMany().HasForeignKey(w=>w.Id).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CaseWorkflow>().Property(w=>w.Version).IsConcurrencyToken();
        model.Entity<CaseWorkflow>().Property(w=>w.FeedbackRound).HasDefaultValue(1);
        model.Entity<CaseWorkflow>().HasOne<Operator>().WithMany().HasForeignKey(w=>w.AssignedOfficerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CaseWorkflow>().HasOne<Operator>().WithMany().HasForeignKey(w=>w.ContractorId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorkflowUpdate>().HasOne<CaseWorkflow>().WithMany().HasForeignKey(w=>w.CaseId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Operator>().Property(u => u.AccountVersion).IsConcurrencyToken();
        model.Entity<Operator>().HasIndex(u => u.Email).IsUnique();
        model.Entity<Operator>().HasIndex(u => u.BootstrapSlot).IsUnique();
        model.Entity<Grievance>().HasIndex(g => g.Reference).IsUnique();
        model.Entity<Grievance>().HasIndex(g => new { g.OwnerId, g.Status });
        model.Entity<Grievance>().Property(g => g.Version).IsConcurrencyToken();
        model.Entity<Grievance>().Property(g => g.CategoryCode).HasDefaultValue("");
        model.Entity<Grievance>().Property(g => g.SubcategoryCode).HasDefaultValue("");
        model.Entity<Grievance>().Property(g => g.RoutingEvidence).HasDefaultValue("");
        model.Entity<Grievance>().Property(g => g.RoutingMethod).HasDefaultValue("");
        model.Entity<Grievance>().HasOne<Operator>().WithMany().HasForeignKey(g => g.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Grievance>().HasMany(g => g.History).WithOne().HasForeignKey(h => h.GrievanceId);
        model.Entity<EvidenceAttachment>().HasIndex(e => new { e.GrievanceId, e.Sha256 }).IsUnique();
        model.Entity<EvidenceAttachment>().HasOne<Grievance>().WithMany().HasForeignKey(e => e.GrievanceId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<InboxMessage>().HasIndex(m => new { m.OwnerId, m.State });
        model.Entity<InboxMessage>().HasIndex(m => new { m.OwnerId, m.Fingerprint });
        model.Entity<InboxMessage>().HasIndex(m => new { m.OwnerId, m.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        model.Entity<InboxMessage>().HasIndex(m => m.GrievanceId).IsUnique().HasFilter("[GrievanceId] IS NOT NULL");
        model.Entity<InboxMessage>().Property(m => m.Version).IsConcurrencyToken();
        model.Entity<InboxMessage>().HasOne<Operator>().WithMany().HasForeignKey(m => m.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<InboxMessage>().HasOne(m => m.Grievance).WithMany().HasForeignKey(m => m.GrievanceId).OnDelete(DeleteBehavior.Restrict);
    }
}
