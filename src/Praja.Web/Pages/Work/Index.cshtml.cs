using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Work;
public class IndexModel(WorkspaceDb db, WorkflowService workflow, NotificationService notifications):PageModel
{
    public OfficerRole Role {get;private set;}=null!;
    public List<Grievance> Cases {get;private set;}=[];
    public Dictionary<Guid,CaseWorkflow> Work {get;private set;}=[];
    public Dictionary<string,int> Counts {get;private set;}=[];
    public List<DepartmentLoad> Departments {get;private set;}=[];
    public int Escalations {get;private set;}
    public int Total {get;private set;}
    public int Pages => Math.Max(1,(int)Math.Ceiling(Total/30d));
    [BindProperty(SupportsGet=true)] public string? Filter {get;set;}
    [BindProperty(SupportsGet=true)] public int PageNumber {get;set;}=1;
    public string Heading => Role.Role switch {"Contractor"=>"My field work", "Department"=>"My department work", "Reviewer"=>"Verification dashboard", "Intake"=>"Intake dashboard", _=>"Admin dashboard"};
    public string Description => Role.Role switch {
        "Contractor"=>"Accept your assigned field work, publish progress and send completion evidence to the department officer.",
        "Department"=>"Accept complaints, assign field work and review contractor evidence before independent verification.",
        "Reviewer"=>"Inspect completed work, check its evidence and verify or return it for correction.",
        "Intake"=>"Review service routes and assign registered complaints to the right department officer.",
        _=>"Monitor department workload, pending assignments and cases that need intervention."};
    public string[] Filters => Role.Role switch {
        "Contractor"=>["Active","InProgress","OfficerReview","Verification","CitizenConfirmation","Closed","All"],
        "Department"=>["Active","Assigned","InProgress","OfficerReview","Overdue","Verification","CitizenConfirmation","Closed","All"],
        "Reviewer"=>["Verification","Overdue","CitizenConfirmation","Closed","All"],
        _=>["Open","Unassigned","Overdue","Assigned","InProgress","OfficerReview","Verification","CitizenConfirmation","Closed","All"]};
    public static string Label(string value)=>value switch {"Active"=>"My active work","Open"=>"Open cases","Unassigned"=>"Awaiting assignment","Overdue"=>"Overdue","Assigned"=>"Awaiting acceptance","InProgress"=>"In progress","OfficerReview"=>"Awaiting officer review","Verification"=>"Awaiting verification","CitizenConfirmation"=>"Awaiting citizen","Closed"=>"Closed",_=>"All cases"};
    private IQueryable<Grievance> Select(IQueryable<Grievance> query,string filter)=>filter switch {
        "Unassigned"=>query.Where(g=>g.Status!="Closed"&&!db.CaseWorkflows.Any(w=>w.Id==g.Id)),
        "Open"=>query.Where(g=>g.Status!="Closed"),
        "Active"=>query.Where(g=>db.CaseWorkflows.Any(w=>w.Id==g.Id&&(w.Stage=="Assigned"||w.Stage=="InProgress"||w.Stage=="OfficerReview"))),
        "Overdue"=>query.Where(g=>db.CaseWorkflows.Any(w=>w.Id==g.Id&&w.Stage!="Closed"&&w.DueUtc<DateTime.UtcNow.Date)),
        "Closed"=>query.Where(g=>g.Status=="Closed"),
        "All"=>query,
        _=>query.Where(g=>db.CaseWorkflows.Any(w=>w.Id==g.Id&&w.Stage==filter))};
    public async Task OnGetAsync()
    {
        Role=await workflow.RoleAsync(User.OwnerId());
        var query=db.Grievances.AsNoTracking().Where(g=>g.Status!="Draft");
        if(Role.Role=="Department")query=query.Where(g=>g.Department==Role.Department&&db.CaseWorkflows.Any(w=>w.Id==g.Id&&w.AssignedOfficerId==User.OwnerId()));
        if(Role.Role=="Contractor")query=query.Where(g=>g.Department==Role.Department&&db.CaseWorkflows.Any(w=>w.Id==g.Id&&w.ContractorId==User.OwnerId()));
        Filter=Filters.Contains(Filter)?Filter:Role.Role switch {"Department" or "Contractor"=>"Active","Reviewer"=>"Verification","Intake"=>"Unassigned",_=>"Open"};
        foreach(var filter in Filters)Counts[filter]=await Select(query,filter).CountAsync();
        Escalations=await notifications.ForOfficer(User.OwnerId()).Where(n=>n.ReadUtc==null&&(n.Severity=="Escalation"||n.Severity=="Urgent")&&(n.EventKey.StartsWith("unassigned:") ? db.Grievances.Any(g=>g.Id==n.CaseId&&g.Status=="Registered")&&!db.CaseWorkflows.Any(w=>w.Id==n.CaseId) : db.CaseWorkflows.Any(w=>w.Id==n.CaseId&&w.Stage!="Closed"))).Select(n=>n.CaseId).Distinct().CountAsync();
        if(Role.Role=="Admin")Departments=await query.Where(g=>g.Status!="Closed").GroupBy(g=>g.Department).OrderByDescending(group=>group.Count()).Select(group=>new DepartmentLoad(group.Key,group.Count())).ToListAsync();
        Total=Counts[Filter!];PageNumber=Math.Clamp(PageNumber,1,Pages);
        Cases=await Select(query,Filter!).OrderBy(g=>db.CaseWorkflows.Where(w=>w.Id==g.Id).Select(w=>(DateTime?)w.DueUtc).FirstOrDefault()??g.CreatedUtc).ThenBy(g=>g.Id).Skip((PageNumber-1)*30).Take(30).ToListAsync();
        var ids=Cases.Select(c=>c.Id).ToArray();Work=await db.CaseWorkflows.AsNoTracking().Where(w=>ids.Contains(w.Id)).ToDictionaryAsync(w=>w.Id);
    }
    public record DepartmentLoad(string Department,int Open);
}
