using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;
using System.Text.Json;
using System.Security.Cryptography;

namespace Praja.Web.Services;

public partial class WorkflowService(WorkspaceDb db, IDataProtectionProvider protection, EvidenceService evidence)
{
    public async Task<OfficerRole> RoleAsync(Guid actor)
    {
        var account=await db.Operators.AsNoTracking().SingleOrDefaultAsync(o=>o.Id==actor) ?? throw new UnauthorizedAccessException();
        if(account.IsDisabled)throw new UnauthorizedAccessException();
        return await db.OfficerRoles.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==actor) ?? new OfficerRole{Id=actor,Role=account.BootstrapSlot==1?"Admin":"Intake"};
    }
    public async Task<bool> CanReadAsync(Guid actor, Grievance item)
    {
        var role=await RoleAsync(actor);
        return role.Role is "Admin" or "Intake" or "Reviewer" || item.Department==role.Department && (role.Role=="Department" && await db.CaseWorkflows.AnyAsync(w=>w.Id==item.Id&&w.AssignedOfficerId==actor) || role.Role=="Contractor" && await db.CaseWorkflows.AnyAsync(w=>w.Id==item.Id&&w.ContractorId==actor));
    }
    public async Task CreateOfficerAsync(Guid actor,string name,string email,string password,string role,string department)
    {
        if((await RoleAsync(actor)).Role!="Admin")throw new UnauthorizedAccessException();
        if(name.Trim().Length is <2 or >100 || email.Length>200 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) || password.Length is <14 or >128 || role is not ("Intake" or "Department" or "Reviewer" or "Contractor"))throw new InvalidOperationException("Enter a name, valid email, password of 14–128 characters and supported role.");
        if((role is "Department" or "Contractor")&&(!CaseRules.Departments.ContainsKey(department)||department=="review"))throw new InvalidOperationException("Choose a department.");
        await WorkspaceIdentity.SetupLock.WaitAsync();
        try{
            var user=new Operator{MustChangePassword=true,Name=name.Trim(),Email=email.Trim().ToLowerInvariant(),BootstrapSlot=await db.Operators.MaxAsync(o=>o.BootstrapSlot)+1};
            user.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(user,password);
            db.AccountAudits.Add(new(){ActorId=actor,TargetId=user.Id,Action="Created officer account: "+role});
            db.Operators.Add(user);db.OfficerRoles.Add(new(){Id=user.Id,Role=role,Department=role is "Department" or "Contractor"?department:""});
            try{await db.SaveChangesAsync();}catch(DbUpdateException){db.ChangeTracker.Clear();throw new InvalidOperationException("Account could not be saved. Check whether that email already exists.");}
        }finally{WorkspaceIdentity.SetupLock.Release();}
    }
    public async Task AssignAsync(Guid actor,Guid id,Guid officer,DateTime due,int version)
    {
        if((await RoleAsync(actor)).Role is not ("Admin" or "Intake"))throw new UnauthorizedAccessException();
        await using var transaction=await db.Database.BeginTransactionAsync();
        var item=await db.Grievances.SingleAsync(g=>g.Id==id);
        var target=await RoleAsync(officer);
        if(item.Version!=version||item.Status is not ("Registered" or "InProgress" or "ClosureReview")||await db.CaseWorkflows.AnyAsync(w=>w.Id==id))throw new InvalidOperationException("Choose an unassigned, registered and open complaint. Refresh the case.");
        if(target.Role!="Department"||target.Department!=item.Department)throw new InvalidOperationException("Select an officer from this complaint's department.");
        if(due.Date<DateTime.UtcNow.Date||due>DateTime.UtcNow.AddYears(1))throw new InvalidOperationException("Choose a target date within the next year.");
        var workflow=new CaseWorkflow{Id=id,AssignedOfficerId=officer,DueUtc=DateTime.SpecifyKind(due.Date,DateTimeKind.Utc),Stage=item.Status=="Registered"?"Assigned":"InProgress"};
        if(item.Status=="ClosureReview"){item.Status="InProgress";item.CitizenFeedback="Pending";}
        var receipt=await db.CitizenReceipts.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id);
        if(receipt is not null)workflow.AccessVersion=receipt.AccessVersion;
        db.CaseWorkflows.Add(workflow);await Save(item,workflow,actor,"Assigned to the department. Target date: "+due.ToString("dd MMM yyyy")+".");await transaction.CommitAsync();
    }
    public async Task ActAsync(Guid actor,Guid id,int version,string action,string note,Guid? photo=null,double? latitude=null,double? longitude=null,DateTime? captured=null,bool checkedEvidence=false)
    {
        var item=await db.Grievances.SingleAsync(g=>g.Id==id);var role=await RoleAsync(actor);
        var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==id);
        if(work.Version!=version)throw new InvalidOperationException("Case changed. Refresh before continuing.");
        note=note.Trim();if(note.Length is <5 or >1500)throw new InvalidOperationException("Write an update between 5 and 1,500 characters.");
        if(action is "Accept" or "Progress" or "Complete")
        {
            if(role.Role!="Department"||work.AssignedOfficerId!=actor||role.Department!=item.Department)throw new UnauthorizedAccessException();
            if(action=="Accept"&&work.Stage=="Assigned"){work.Stage="InProgress";item.Status="InProgress";}
            else if(action=="Progress"&&work.Stage=="InProgress"){}
            else if(action=="Complete"&&work.Stage=="InProgress")
            {
                if(work.ContractorId is not null)throw new InvalidOperationException("The assigned contractor must submit field evidence for officer review.");
                if(!checkedEvidence||latitude is null||longitude is null||!double.IsFinite(latitude.Value)||!double.IsFinite(longitude.Value)||latitude is <-90 or >90||longitude is <-180 or >180||captured is null||captured>DateTime.UtcNow.AddMinutes(5)||captured<item.CreatedUtc.AddDays(-1))throw new InvalidOperationException("Provide the photo capture time in UTC, valid coordinates and permission to share this evidence with the citizen.");
                if(photo==item.IncidentPhotoId && photo is not null)throw new InvalidOperationException("Upload a completion photo; the original incident photo cannot demonstrate the repair.");
                if(work.ReopenedUtc is DateTime reopened && (captured < reopened || !await db.EvidenceAttachments.AnyAsync(e=>e.Id==photo&&e.CreatedUtc>reopened)))throw new InvalidOperationException("Reopened work needs a newly uploaded completion photo captured after reopening.");
                if(!await db.EvidenceAttachments.AnyAsync(e=>e.Id==photo&&e.GrievanceId==id&&(e.ContentType=="image/jpeg"||e.ContentType=="image/png")))throw new InvalidOperationException("Upload and select a completion photo for this complaint.");
                work.CompletedBy=actor;work.CompletionPhotoId=photo;work.Latitude=latitude;work.Longitude=longitude;work.CapturedUtc=captured;work.CompletionNote=note;work.VerifiedBy=null;
                work.Stage="Verification";item.Status="ClosureReview";item.ResolutionReport=note;item.CitizenFeedback="Pending";
            }
            else throw new InvalidOperationException("Action is not available at this stage.");
        }
        else if(action is "FieldAccept" or "FieldProgress" or "FieldComplete")
        {
            if(role.Role!="Contractor"||work.ContractorId!=actor||role.Department!=item.Department)throw new UnauthorizedAccessException();
            if(work.Stage!="InProgress")throw new InvalidOperationException("Field actions require active department work.");
            if(action=="FieldAccept")
            {
                if(work.ContractorAccepted)throw new InvalidOperationException("This field assignment was already accepted.");
                work.ContractorAccepted=true;
            }
            else
            {
                if(!work.ContractorAccepted)throw new InvalidOperationException("Accept the field assignment before updating work.");
                if(action=="FieldComplete")
                {
                    if(!checkedEvidence||latitude is null||longitude is null||!double.IsFinite(latitude.Value)||!double.IsFinite(longitude.Value)||latitude is <-90 or >90||longitude is <-180 or >180||captured is null||captured>DateTime.UtcNow.AddMinutes(5)||captured<item.CreatedUtc.AddDays(-1))throw new InvalidOperationException("Provide the photo capture time in UTC, valid coordinates and permission to share this evidence with the citizen.");
                    if(photo==item.IncidentPhotoId && photo is not null)throw new InvalidOperationException("Upload a completion photo; the original incident photo cannot demonstrate the repair.");
                    if(work.ReopenedUtc is DateTime reopened && (captured < reopened || !await db.EvidenceAttachments.AnyAsync(e=>e.Id==photo&&e.CreatedUtc>reopened)))throw new InvalidOperationException("Reopened work needs a newly uploaded completion photo captured after reopening.");
                if(!await db.EvidenceAttachments.AnyAsync(e=>e.Id==photo&&e.GrievanceId==id&&(e.ContentType=="image/jpeg"||e.ContentType=="image/png")))throw new InvalidOperationException("Upload and select a completion photo for this complaint.");
                    work.CompletedBy=actor;work.CompletionPhotoId=photo;work.Latitude=latitude;work.Longitude=longitude;work.CapturedUtc=captured;work.CompletionNote=note;work.VerifiedBy=null;
                    work.Stage="OfficerReview";item.Status="ClosureReview";item.ResolutionReport=note;item.CitizenFeedback="Pending";
                }
            }
        }
        else if(action is "ApproveField" or "ReturnField")
        {
            if(role.Role!="Department"||work.AssignedOfficerId!=actor||role.Department!=item.Department)throw new UnauthorizedAccessException();
            if(work.Stage!="OfficerReview"||work.ContractorId is null||work.CompletedBy!=work.ContractorId)throw new InvalidOperationException("Only submitted contractor work can receive officer review.");
            if(action=="ApproveField")
            {
                if(!checkedEvidence||work.CompletionPhotoId is null||await evidence.DownloadAsync(id,work.CompletionPhotoId.Value,item.OwnerId) is null)throw new InvalidOperationException("Inspect the contractor photo, coordinates and report, then confirm officer review.");
                work.Stage="Verification";item.Status="ClosureReview";
            }
            else{work.Stage="InProgress";item.Status="InProgress";}
        }
        else if(action is "Verify" or "Return")
        {
            if(role.Role!="Reviewer"||actor==work.AssignedOfficerId||actor==work.CompletedBy)throw new UnauthorizedAccessException();
            if(work.Stage!="Verification")throw new InvalidOperationException("Only completed work awaiting verification can be reviewed.");
            if(action=="Verify")
            {
                if(!checkedEvidence||work.CompletionPhotoId is null||await evidence.DownloadAsync(id,work.CompletionPhotoId.Value,item.OwnerId) is null)throw new InvalidOperationException("Inspect the completion photo, coordinates and report, then confirm verification.");
                work.Stage="CitizenConfirmation";work.VerifiedBy=actor;item.Status="AwaitingCitizen";
            }
            else{work.Stage="InProgress";work.VerifiedBy=null;item.Status="InProgress";}
        }
        else throw new InvalidOperationException("Unknown workflow action.");
        await Save(item,work,actor,note);
    }
    private async Task Save(Grievance item,CaseWorkflow work,Guid actor,string note)
    {
        var name=await db.Operators.Where(o=>o.Id==actor).Select(o=>o.Name).SingleAsync();
        work.Version++;item.Version++;item.UpdatedUtc=DateTime.UtcNow;
        db.WorkflowUpdates.Add(new(){CaseId=item.Id,Stage=work.Stage,Actor=name,Note=note});
        db.AuditEntries.Add(new(){GrievanceId=item.Id,Actor=name,Action="Workflow: "+work.Stage+". "+note[..Math.Min(note.Length,400)]});
        await new NotificationService(db).QueueAsync(item,work,$"workflow:{work.Version}","Case update: "+CaseRules.WorkflowLabel(work.Stage)+". Open the case for details.");
        await Persist();
    }
    private async Task Persist(){try{await db.SaveChangesAsync();}catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();throw new InvalidOperationException("Case changed in another session. Refresh and retry.");}}
    public async Task<string> IssueAsync(Guid actor,Guid id)
    {
        if((await RoleAsync(actor)).Role is not ("Admin" or "Intake"))throw new UnauthorizedAccessException();
        var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==id);work.AccessVersion=Guid.NewGuid();work.Version++;
        await Persist();
        return protection.CreateProtector("Praja.WorkflowCitizen.v1").Protect(JsonSerializer.Serialize(new Ticket(id,work.AccessVersion,DateTimeOffset.UtcNow.AddDays(7))));
    }
    public async Task<CaseWorkflow?> CitizenAsync(string? token)
    {
        if(string.IsNullOrWhiteSpace(token)||token.Length>4096)return null;
        try{var ticket=JsonSerializer.Deserialize<Ticket>(protection.CreateProtector("Praja.WorkflowCitizen.v1").Unprotect(token));
            if(ticket is null||ticket.Expires<=DateTimeOffset.UtcNow||ticket.Expires>DateTimeOffset.UtcNow.AddDays(7))return null;
            return await db.CaseWorkflows.SingleOrDefaultAsync(w=>w.Id==ticket.Id&&w.AccessVersion==ticket.Version);
        }catch(Exception ex)when(ex is CryptographicException or JsonException or FormatException){return null;}
    }
    public async Task<Grievance?> CitizenCaseAsync(string? token)
    {
        var ticket=ReadTicket(token);if(ticket is null)return null;
        var work=await db.CaseWorkflows.AsNoTracking().SingleOrDefaultAsync(w=>w.Id==ticket.Id);
        var valid=work is not null?work.AccessVersion==ticket.Version:await db.CitizenReceipts.AnyAsync(r=>r.Id==ticket.Id&&r.AccessVersion==ticket.Version);
        return valid?await db.Grievances.AsNoTracking().SingleOrDefaultAsync(g=>g.Id==ticket.Id):null;
    }
    public string ReceiptCode(CitizenReceipt receipt)=>protection.CreateProtector("Praja.WorkflowCitizen.v1").Protect(JsonSerializer.Serialize(new Ticket(receipt.Id,receipt.AccessVersion,new DateTimeOffset(DateTime.SpecifyKind(receipt.CreatedUtc,DateTimeKind.Utc)).AddDays(7))));
    private Ticket? ReadTicket(string? token)
    {
        if(string.IsNullOrWhiteSpace(token)||token.Length>4096)return null;
        try{var ticket=JsonSerializer.Deserialize<Ticket>(protection.CreateProtector("Praja.WorkflowCitizen.v1").Unprotect(token));return ticket is not null&&ticket.Expires>DateTimeOffset.UtcNow&&ticket.Expires<=DateTimeOffset.UtcNow.AddDays(7)?ticket:null;}
        catch(Exception ex)when(ex is CryptographicException or JsonException or FormatException){return null;}
    }
    public async Task RouteAsync(Guid actor,Guid id,int version,string department)
    {
        if((await RoleAsync(actor)).Role is not ("Admin" or "Intake"))throw new UnauthorizedAccessException();
        var item=await db.Grievances.SingleAsync(g=>g.Id==id);
        if(item.Version!=version||item.Status!="Registered"||await db.CaseWorkflows.AnyAsync(w=>w.Id==id)||!CaseRules.Departments.ContainsKey(department)||department=="review")throw new InvalidOperationException("Choose a department for an unassigned registered complaint and refresh if it changed.");
        item.Department=department;item.CategoryCode="";item.SubcategoryCode="";item.RoutingMethod="Intake officer reviewed";item.Version++;item.UpdatedUtc=DateTime.UtcNow;
        db.AuditEntries.Add(new(){GrievanceId=id,Actor="Intake officer",Action="Reviewed service route: "+CaseRules.Departments[department]});await Persist();
    }
    public async Task RespondAsync(string? token,int version,bool resolved,string note)
    {
        var work=await CitizenAsync(token)??throw new UnauthorizedAccessException();
        if(work.Version!=version||work.Stage!="CitizenConfirmation"||work.VerifiedBy is null)throw new InvalidOperationException("This case is not awaiting your confirmation. Refresh its status.");
        note=note.Trim();if(note.Length>1500||!resolved&&note.Length<5)throw new InvalidOperationException("For a dispute, describe what remains unresolved (5–1,500 characters).");
        var item=await db.Grievances.SingleAsync(g=>g.Id==work.Id);
        work.Stage=resolved?"Closed":"InProgress";work.Version++;item.Status=resolved?"Closed":"InProgress";item.CitizenFeedback=resolved?"Confirmed":"Disagreed";item.ClosedUtc=resolved?DateTime.UtcNow:null;item.Version++;item.UpdatedUtc=DateTime.UtcNow;
        if(!resolved){work.VerifiedBy=null;work.FeedbackRound++;}
        var message=(resolved?"Citizen confirmed resolution. ":"Citizen disputed resolution; returned to department. ")+note;
        db.WorkflowUpdates.Add(new(){CaseId=item.Id,Stage=work.Stage,Actor="Citizen (private access code)",Note=message[..Math.Min(1500,message.Length)]});
        db.AuditEntries.Add(new(){GrievanceId=item.Id,Actor="Citizen (private access code)",Action=message[..Math.Min(500,message.Length)]});
        await new NotificationService(db).QueueAsync(item,work,$"workflow:{work.Version}",resolved?"Citizen confirmed resolution. Case closed.":"Citizen disputed resolution. Further department work required.");
        await Persist();
    }
    private sealed record Ticket(Guid Id,Guid Version,DateTimeOffset Expires);
}
