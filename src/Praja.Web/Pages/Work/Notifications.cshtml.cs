using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Work;

public class NotificationsModel(NotificationService notifications, WorkflowService workflow, TimeProvider clock) : PageModel
{
    public List<CaseNotification> Alerts { get; private set; } = [];
    public bool CanCheck { get; private set; }
    [TempData] public string? Notice { get; set; }
    public async Task OnGetAsync()
    {
        CanCheck = (await workflow.RoleAsync(User.OwnerId())).Role is "Admin" or "Intake";
        Alerts = await notifications.ForOfficer(User.OwnerId()).AsNoTracking().OrderBy(n => n.ReadUtc != null)
            .ThenByDescending(n => n.CreatedUtc).ThenByDescending(n => n.Id).Take(100).ToListAsync();
    }
    public async Task<IActionResult> OnPostReadAsync(long id)
    {
        if (!await notifications.MarkReadAsync(User.OwnerId(), id)) return NotFound();
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostCheckAsync()
    {
        if ((await workflow.RoleAsync(User.OwnerId())).Role is not ("Admin" or "Intake")) return Forbid();
        var count = await notifications.CheckDeadlinesAsync(clock.GetUtcNow().UtcDateTime, HttpContext.RequestAborted);
        Notice = $"Deadline check finished: {count} new case milestone(s). Existing alerts were not duplicated.";
        return RedirectToPage();
    }
}
