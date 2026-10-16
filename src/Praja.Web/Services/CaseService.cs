using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;

namespace Praja.Web.Services;

public class CaseService(WorkspaceDb db, DraftingService drafts)
{
    public async Task<Grievance> CreateAsync(Guid owner, string actor, string original, string language, string district, string location, string source, bool consent, CancellationToken cancellation = default, RoutingSuggestion? intakeRoute = null, FollowUpAnswers? followUp = null)
    {
        var item = await PrepareAsync(owner, actor, original, language, district, location, source, consent, cancellation, intakeRoute, followUp);
        db.Grievances.Add(item); await db.SaveChangesAsync(cancellation); return item;
    }
    public async Task<Grievance> CreateFromInboxAsync(Guid id, int version, Guid owner, string actor, string original, string language, string district, string location, bool consent, CancellationToken cancellation = default, RoutingSuggestion? intakeRoute = null, FollowUpAnswers? followUp = null, DraftResult? preparedDraft = null)
    {
        var source = await db.InboxMessages.SingleOrDefaultAsync(m => m.Id == id && m.OwnerId == owner, cancellation)
            ?? throw new InvalidOperationException("Inbox item not found.");
        if (source.State != "Pending" || source.Version != version) throw new InvalidOperationException("This inbox item changed or was already processed. Reload it before preparing a draft.");
        if (source.OriginalText != original.Trim()) throw new InvalidOperationException("The inbox original is preserved. Review its draft instead of replacing source text.");
        if (PublicSourceCollection.UsesLocalReview(source.CapturedBy) && !consent)
            throw new InvalidOperationException("Confirm permission to retain and review this public report.");
        if (PublicSourceCollection.UsesLocalReview(source.CapturedBy))
            preparedDraft = DraftingService.Template(source.OriginalText, language, district, location, "Local template · public source retained for review");
        var item = await PrepareAsync(owner, actor, source.OriginalText, language, district, location, source.Channel, consent, cancellation, intakeRoute, followUp, preparedDraft);
        if (PublicSourceCollection.UsesLocalReview(source.CapturedBy))
        {
            var capture = item.History.First(h => h.Action == "Original complaint recorded with consent; draft prepared for officer review.");
            capture.Action = "Public-source report retained with officer review permission; draft prepared for verification. Citizen consent or direct submission was not recorded.";
        }
        item.IsSample = InboxImportService.IsDemoSample(source);
        if (item.IsSample) item.History.Add(new() { Actor = actor, Action = "Fictional channel-import sample preserved as a sample case; normal analytics excludes samples by default." });
        item.History.Add(new() {Actor = actor, Action = $"Original retained from inbox item {source.Id}; source channel and references remain available in the intake inbox."});
        if(source.ReviewedUtc.HasValue&&source.ReviewNote.Length>0)item.History.Add(new(){Actor=source.ReviewedBy,Action="Incoming duplicate review kept separate: "+source.ReviewNote[..Math.Min(450,source.ReviewNote.Length)]});
        source.State = "Converted"; source.Grievance = item; source.GrievanceId = item.Id; source.Version++;
        source.ReviewedBy = actor; source.ReviewedUtc = DateTime.UtcNow; source.ReviewNote = "Draft prepared; registration and final routing confirmation remain required.";
        db.Grievances.Add(item);
        try { await db.SaveChangesAsync(cancellation); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); throw new InvalidOperationException("This inbox item changed. No additional draft was saved; reload it."); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); throw; }
        return item;
    }
    public Task<InboxMessage?> FindInboxSourceAsync(Guid id, Guid owner) => db.InboxMessages.AsNoTracking().SingleOrDefaultAsync(m => m.GrievanceId == id && m.OwnerId == owner);
    public Task<List<InboxDuplicate>> FindDuplicateCandidatesAsync(Grievance item)=>new InboxService(db).DuplicatesAsync(new InboxMessage{OwnerId=item.OwnerId,GrievanceId=item.Id,OriginalText=item.OriginalText,Fingerprint=InboxService.Fingerprint(item.OriginalText)});
    public Task<List<InboxMessage>> FindDuplicateSourcesAsync(Guid id,Guid owner)=>db.InboxMessages.AsNoTracking().Where(m=>m.DuplicateOfId==id&&m.OwnerId==owner).OrderByDescending(m=>m.ReviewedUtc).Take(100).ToListAsync();
    private async Task<Grievance> PrepareAsync(Guid owner, string actor, string original, string language, string district, string location, string source, bool consent, CancellationToken cancellation, RoutingSuggestion? intakeRoute, FollowUpAnswers? followUp, DraftResult? preparedDraft = null)
    {
        if (!consent) throw new InvalidOperationException("Confirm consent to record and process this complaint.");
        if (original.Trim().Length is < 20 or > 6000 || district.Trim().Length is < 2 or > 100 || location.Trim().Length is < 2 or > 200)
            throw new InvalidOperationException("Enter the original complaint, district and location within the displayed limits.");
        if (language is not ("English" or "Kannada") || !InboxService.Channels.Contains(source))
            throw new InvalidOperationException("Choose a supported language and intake channel.");
        var followUpSection = followUp?.DraftSection(language) ?? "";
        var route = intakeRoute ?? CaseRules.SuggestDepartment(original);
        if (!CaseRules.Departments.ContainsKey(route.Department) || route.Reason.Length > 500)
            throw new InvalidOperationException("Review the suggested pilot route before preparing the draft.");
        if (!PilotRoutingCatalog.ValidSelection(route.Department, route.Category, route.Subcategory)
            || route.Evidence.Length > 250 || (route.Evidence.Length > 0 && !original.Contains(route.Evidence, StringComparison.Ordinal)) || route.Method.Length > 120)
            throw new InvalidOperationException("Review the demo category and original routing evidence before preparing the draft.");
        var draft = preparedDraft ?? await drafts.DraftAsync(original.Trim(), language, district.Trim(), location.Trim(), cancellation);
        if(draft.Title.Length is <5 or >160||draft.Text.Length is <20 or >8000||draft.Summary.Length>1800||draft.Method.Length>200)throw new InvalidOperationException("Review the prepared draft within the displayed limits.");
        var item = new Grievance
        {
            OwnerId = owner, Reference = $"PRJ-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            OriginalText = original.Trim(), DraftText = draft.Text + followUpSection, Title = draft.Title, Summary = draft.Summary,
            Department = route.Department, RoutingReason = route.Reason, DraftMethod = draft.Method,
            CategoryCode = route.Category, SubcategoryCode = route.Subcategory, RoutingEvidence = route.Evidence, RoutingMethod = route.Method,
            Language = language, District = district.Trim(), Location = location.Trim(), Source = source
        };
        item.History.Add(new() { Actor = actor, Action = "Original complaint recorded with consent; draft prepared for officer review." });
        if (followUpSection.Length > 0) item.History.Add(new() { Actor = actor, Action = "Citizen timeline/requested-help follow-up answers confirmed by the officer and added separately to the draft. Original wording is unchanged; these answers were not sent to the AI service." });
        if (intakeRoute is not null) item.History.Add(new() { Actor = actor, Action = "Reviewed intake suggestions carried into draft preparation; final routing confirmation remains required." });
        item.History.Add(new() { Actor = actor, Action = $"Initial demo routing suggestion: {CaseRules.Departments[route.Department]}; category: {PilotRoutingCatalog.CategoryLabel(route.Category)}; issue: {PilotRoutingCatalog.IssueLabel(route.Subcategory)}. Officer confirmation remains required." });
        return item;
    }

    public Task<Grievance?> FindAsync(Guid id, Guid owner) => db.Grievances.Include(g => g.History).SingleOrDefaultAsync(g => g.Id == id && g.OwnerId == owner);

    public async Task RegisterAsync(Grievance item, int expectedVersion, string title, string draft, string department, bool reviewed, string actor, string? categoryCode = null, string? subcategoryCode = null)
    {
        CheckVersion(item, expectedVersion);
        CaseRules.ValidateTransition(item.Status, "Registered", reviewed);
        if (title.Trim().Length is < 5 or > 160 || draft.Trim().Length is < 20 or > 8000 || !CaseRules.Departments.ContainsKey(department) || department == "review")
            throw new InvalidOperationException("Review the title and complaint, then choose a specific pilot department.");
        var category = categoryCode?.Trim() ?? (department == item.Department ? item.CategoryCode : "");
        var issue = subcategoryCode?.Trim() ?? (department == item.Department && category == item.CategoryCode ? item.SubcategoryCode : "");
        if (!PilotRoutingCatalog.ValidSelection(department, category, issue))
            throw new InvalidOperationException("Choose a demo category belonging to the selected service and an issue belonging to that category, or leave them unclassified.");
        item.Title = title.Trim(); item.DraftText = draft.Trim(); item.Department = department;
        item.CategoryCode = category; item.SubcategoryCode = issue;
        item.Status = "Registered";
        await new NotificationService(db).QueueAsync(item,null,"registered","Complaint registered. Awaiting department assignment.");
        await SaveAsync(item, actor, $"Officer confirmed draft and route: {CaseRules.Departments[department]}; category: {PilotRoutingCatalog.CategoryLabel(category)}; issue: {PilotRoutingCatalog.IssueLabel(issue)}. Registered in the local workspace; IPGRS not connected.");
    }

    public async Task StartAsync(Grievance item, int expectedVersion, string actor)
    {
        await RequireLegacyWorkflow(item.Id);
        CheckVersion(item, expectedVersion);
        CaseRules.ValidateTransition(item.Status, "InProgress", false);
        item.Status = "InProgress";
        await SaveAsync(item, actor, "Officer started casework.");
    }

    public async Task ReviewAsync(Grievance item, int expectedVersion, string report, string evidence, string feedback, string actor)
    {
        await RequireLegacyWorkflow(item.Id);
        CheckVersion(item, expectedVersion);
        CaseRules.ValidateTransition(item.Status, "ClosureReview", false);
        if (report.Trim().Length is < 20 or > 6000 || evidence.Length > 500 || feedback is not ("Pending" or "Confirmed" or "Disagreed"))
            throw new InvalidOperationException("Enter a resolution report and select the citizen feedback state.");
        item.ResolutionReport = report.Trim(); item.EvidenceReference = evidence.Trim(); item.CitizenFeedback = feedback;
        item.Status = "ClosureReview";
        await SaveAsync(item, actor, "Resolution report recorded; document checklist prepared for human review.");
    }

    public async Task CloseAsync(Grievance item, int expectedVersion, bool reviewed, string actor)
    {
        await RequireLegacyWorkflow(item.Id);
        CheckVersion(item, expectedVersion);
        CaseRules.ValidateTransition(item.Status, "Closed", reviewed, CaseRules.AssessClosure(item.OriginalText, item.ResolutionReport, item.EvidenceReference, item.CitizenFeedback));
        item.Status = "Closed"; item.ClosedUtc = DateTime.UtcNow;
        await SaveAsync(item, actor, "Officer confirmed evidence and citizen feedback and closed the local case. This is not an IPGRS closure.");
    }

    public async Task RecordCitizenAccessAsync(Grievance item, int expectedVersion, string actor)
    {
        CheckVersion(item, expectedVersion);
        if (item.Status == "Draft") throw new InvalidOperationException("Register the complaint before issuing citizen access.");
        await SaveAsync(item, actor, "Private citizen access code issued for limited local status, valid for seven days. The code itself is not stored in the audit history.");
    }

    private static void CheckVersion(Grievance item, int expectedVersion)
    {
        if (item.Version != expectedVersion) throw new InvalidOperationException("This case changed in another tab. Reload it before making changes.");
    }
    private async Task RequireLegacyWorkflow(Guid id)
    {
        if(await db.CaseWorkflows.AnyAsync(w=>w.Id==id))throw new InvalidOperationException("Use Team casework for this assigned complaint. Independent verification and citizen confirmation are required.");
    }
    private async Task SaveAsync(Grievance item, string actor, string action)
    {
        item.Version++; item.UpdatedUtc = DateTime.UtcNow;
        item.History.Add(new() { Actor = actor, Action = action });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            // Discard the failed write and unsaved audit entries before the caller reloads.
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("Another officer updated this case. Reload it before making changes.");
        }
    }
}
