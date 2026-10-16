using Microsoft.EntityFrameworkCore;
namespace Praja.Web.Services;

public partial class WorkflowService
{
    public async Task AssignContractorAsync(Guid actor,Guid id,Guid contractor,int version,string reason)
    {
        var role=await RoleAsync(actor);
        await using var transaction=await db.Database.BeginTransactionAsync();
        var item=await db.Grievances.SingleAsync(g=>g.Id==id);
        var work=await db.CaseWorkflows.SingleAsync(w=>w.Id==id);
        if(role.Role!="Department"||work.AssignedOfficerId!=actor||role.Department!=item.Department)throw new UnauthorizedAccessException();
        if(work.Version!=version)throw new InvalidOperationException("Case changed. Refresh before assigning field work.");
        if(work.Stage!="InProgress")throw new InvalidOperationException("Assign contractors only after accepting the complaint and while work is in progress.");
        reason=reason.Trim();if(reason.Length is <5 or >1000)throw new InvalidOperationException("Give field instructions or a reassignment reason of 5–1,000 characters.");
        var target=await db.Operators.AsNoTracking().SingleOrDefaultAsync(o=>o.Id==contractor&&!o.IsDisabled);
        var grant=await db.OfficerRoles.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==contractor);
        if(target is null||grant?.Role!="Contractor"||grant.Department!=item.Department||contractor==work.ContractorId)throw new InvalidOperationException("Choose a different active contractor from this department.");
        var previous=work.ContractorId is null?null:await db.Operators.Where(o=>o.Id==work.ContractorId).Select(o=>o.Name).SingleAsync();
        work.ContractorId=contractor;work.ContractorAccepted=false;
        // Keep earlier evidence and its author as history; new assignment requires fresh field acceptance.
        await Save(item,work,actor,$"Field work {(previous is null?"assigned":"reassigned from "+previous)} to {target.Name}. Contractor acceptance required. {reason}");
        await transaction.CommitAsync();
    }
}
