using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;
namespace Praja.Web.Services;
public partial class WorkflowService
{
    public async Task ManageOfficerAsync(Guid actor,Guid target,int version,string action,string name,string email,string role,string department,string password,string reason)
    {
        if((await RoleAsync(actor)).Role!="Admin")throw new UnauthorizedAccessException();
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=await db.Operators.SingleOrDefaultAsync(o=>o.Id==target)??throw new InvalidOperationException("Account not found.");
        if(user.Id==actor||user.BootstrapSlot==1)throw new InvalidOperationException("The workspace administrator is protected. Use local password recovery for that account.");
        if(user.AccountVersion!=version)throw new InvalidOperationException("Account changed. Refresh and retry.");
        reason=reason.Trim();
        if(reason.Length is <5 or >300)throw new InvalidOperationException("Provide an administrative reason of 5–300 characters.");
        var grant=await db.OfficerRoles.SingleOrDefaultAsync(o=>o.Id==target);
        var previousRole=grant?.Role??"Intake";var previousDepartment=grant?.Department??"";
        if(action=="Disable" || action=="Edit"&&(role!=previousRole||department!=previousDepartment))
            if(await db.CaseWorkflows.AnyAsync(w=>(w.AssignedOfficerId==target||w.ContractorId==target)&&w.Stage!="Closed"))
                throw new InvalidOperationException("Reassign this officer's open cases before disabling or changing their role or department.");
        switch(action)
        {
            case "Edit":
                name=name.Trim();email=email.Trim().ToLowerInvariant();
                if(name.Length is <2 or >100||email.Length>200||!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)||role is not ("Intake" or "Department" or "Reviewer" or "Contractor"))throw new InvalidOperationException("Enter a valid name, email and officer role.");
                if((role is "Department" or "Contractor")&&(!CaseRules.Departments.ContainsKey(department)||department=="review"))throw new InvalidOperationException("Choose a department.");
                if(await db.Operators.AnyAsync(o=>o.Id!=target&&o.Email==email))throw new InvalidOperationException("That email is already used by another account.");
                grant??=new OfficerRole{Id=target};if(db.Entry(grant).State==EntityState.Detached)db.OfficerRoles.Add(grant);
                grant.Role=role;grant.Department=role is "Department" or "Contractor"?department:"";user.Name=name;user.Email=email;break;
            case "Disable":user.IsDisabled=true;break;
            case "Enable":user.IsDisabled=false;break;
            case "Reset":
                if(password.Length is <14 or >128)throw new InvalidOperationException("Use a new password of 14–128 characters.");
                user.MustChangePassword=true;user.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(user,password);user.FailedLogins=0;user.LockedUntilUtc=null;break;
            default:throw new InvalidOperationException("Unknown account action.");
        }
        user.AccountVersion++;
        db.AccountAudits.Add(new(){ActorId=actor,TargetId=target,Action=action+". "+reason});
        try{await db.SaveChangesAsync();await transaction.CommitAsync();}
        catch(DbUpdateException){db.ChangeTracker.Clear();throw new InvalidOperationException("Account changed or could not be saved. Refresh and check the email before retrying.");}
    }
    public async Task TransferAsync(Guid actor,Guid id,Guid officer,int version,string reason)
    {
        if((await RoleAsync(actor)).Role is not ("Admin" or "Intake"))throw new UnauthorizedAccessException();
        await using var transaction=await db.Database.BeginTransactionAsync();
        var item=await db.Grievances.SingleAsync(g=>g.Id==id);
        var work=await db.CaseWorkflows.SingleOrDefaultAsync(w=>w.Id==id)??throw new InvalidOperationException("Assign the complaint before transferring it.");
        if(work.Version!=version)throw new InvalidOperationException("Case changed. Refresh before transferring.");
        if(work.Stage is not ("Assigned" or "InProgress"))throw new InvalidOperationException("Transfer is available only for assigned or active work. Verification or citizen review must return the case for further work first. Closed cases cannot transfer.");
        reason=reason.Trim();if(reason.Length is <5 or >1000)throw new InvalidOperationException("Give a transfer reason of 5–1,000 characters.");
        var target=await db.Operators.AsNoTracking().SingleOrDefaultAsync(o=>o.Id==officer&&!o.IsDisabled);
        var grant=await db.OfficerRoles.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==officer);
        if(target is null||grant?.Role!="Department"||grant.Department==item.Department||grant.Department=="review"||!CaseRules.Departments.ContainsKey(grant.Department))throw new InvalidOperationException("Choose an active officer from a different department.");
        var previousDepartment=CaseRules.Departments[item.Department];
        var previousOfficer=await db.Operators.Where(o=>o.Id==work.AssignedOfficerId).Select(o=>o.Name).SingleAsync();
        item.Department=grant.Department;item.CategoryCode="";item.SubcategoryCode="";item.RoutingEvidence="";
        item.RoutingMethod="Cross-department transfer reviewed by intake";item.RoutingReason=reason[..Math.Min(500,reason.Length)];
        item.Status="Registered";item.CitizenFeedback="Pending";
        work.AssignedOfficerId=officer;work.Stage="Assigned";work.VerifiedBy=null;work.ContractorId=null;work.ContractorAccepted=false;
        // Existing reports, files and completion provenance remain historical evidence, not approval.
        await Save(item,work,actor,$"Transferred from {previousDepartment} ({previousOfficer}) to {CaseRules.Departments[grant.Department]} ({target.Name}). New officer acceptance required; target date unchanged. Reason: {reason}");
        await transaction.CommitAsync();
    }
    public async Task ReassignAsync(Guid actor,Guid id,Guid officer,int version,string reason)
    {
        if((await RoleAsync(actor)).Role is not ("Admin" or "Intake"))throw new UnauthorizedAccessException();
        await using var transaction=await db.Database.BeginTransactionAsync();
        var item=await db.Grievances.SingleAsync(g=>g.Id==id);var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==id);
        reason=reason.Trim();
        if(work.Version!=version)throw new InvalidOperationException("Case changed. Refresh before reassigning.");
        if(work.Stage=="Closed")throw new InvalidOperationException("Closed cases cannot be reassigned.");
        if(reason.Length is <5 or >1000)throw new InvalidOperationException("Give a reassignment reason of 5–1,000 characters.");
        var target=await RoleAsync(officer);
        if(officer==work.AssignedOfficerId||target.Role!="Department"||target.Department!=item.Department)throw new InvalidOperationException("Choose a different active officer from this department.");
        var old=await db.Operators.Where(o=>o.Id==work.AssignedOfficerId).Select(o=>o.Name).SingleAsync();
        var next=await db.Operators.Where(o=>o.Id==officer).Select(o=>o.Name).SingleAsync();
        if(work.CompletionPhotoId is not null)work.CompletedBy??=work.AssignedOfficerId;
        work.AssignedOfficerId=officer;
        // Verification and citizen confirmation remain in their current stage; reassignment never approves work.
        await Save(item,work,actor,$"Responsibility reassigned from {old} to {next}. Target date unchanged. {reason}");
        await transaction.CommitAsync();
    }
}
