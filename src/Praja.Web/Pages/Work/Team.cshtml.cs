using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Work;
public class TeamModel(WorkspaceDb db,WorkflowService workflow):PageModel
{
    [BindProperty]public string Name {get;set;}="";
    [BindProperty]public string Email {get;set;}="";
    [BindProperty]public string Password {get;set;}="";
    [BindProperty]public string Role {get;set;}="Intake";
    [BindProperty]public string Department {get;set;}="";
    [BindProperty]public Guid Target {get;set;}
    [BindProperty]public int Version {get;set;}
    [BindProperty]public string? Action {get;set;}
    [BindProperty]public string? Reason {get;set;}
    public List<AccountAudit> Audit {get;private set;}=[];
    public async Task<IActionResult> OnPostManageAsync()
    {
        try{await workflow.ManageOfficerAsync(User.OwnerId(),Target,Version,Action??"",Name??"",Email??"",Role??"",Department??"",Password??"",Reason??"");return RedirectToPage();}
        catch(UnauthorizedAccessException){return Forbid();}
        catch(InvalidOperationException ex){db.ChangeTracker.Clear();ModelState.Clear();Password="";ModelState.AddModelError("",ex.Message);await Load();return Page();}
    }
    public List<Operator> Officers {get;private set;}=[];
    public Dictionary<Guid,OfficerRole> Roles {get;private set;}=[];
    public async Task<IActionResult> OnGetAsync(){if((await workflow.RoleAsync(User.OwnerId())).Role!="Admin")return Forbid();await Load();return Page();}
    public async Task<IActionResult> OnPostAsync()
    {
        try{await workflow.CreateOfficerAsync(User.OwnerId(),Name,Email,Password,Role,Department);return RedirectToPage();}
        catch(UnauthorizedAccessException){return Forbid();}
        catch(InvalidOperationException ex){ModelState.Clear();Password="";ModelState.AddModelError("",ex.Message);await Load();return Page();}
    }
    private async Task Load(){Audit=await db.AccountAudits.AsNoTracking().OrderByDescending(a=>a.AtUtc).Take(50).ToListAsync();Officers=await db.Operators.AsNoTracking().ToListAsync();Roles=await db.OfficerRoles.AsNoTracking().ToDictionaryAsync(r=>r.Id);}
}
