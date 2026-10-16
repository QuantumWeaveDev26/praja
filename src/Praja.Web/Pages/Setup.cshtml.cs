using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;

namespace Praja.Web.Pages;

public class SetupModel(WorkspaceDb db, IWebHostEnvironment environment, IConfiguration config) : PageModel
{
    [BindProperty] public SetupInput Input { get; set; } = new();
    public class SetupInput
    {
        [Required, StringLength(100, MinimumLength = 2)] public string Name { get; set; } = "";
        [Required, EmailAddress, StringLength(200)] public string Email { get; set; } = "";
        [Required, StringLength(128, MinimumLength = 14)] public string Password { get; set; } = "";
        public bool LoadDemo { get; set; } = true;
    }
    private bool Allowed => environment.IsEnvironment("Testing") || environment.IsDevelopment() && (HttpContext.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(HttpContext.Connection.RemoteIpAddress));
    public async Task<IActionResult> OnGetAsync() => !Allowed || await db.Operators.AnyAsync() ? NotFound() : Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (!Allowed) return NotFound();
        await WorkspaceIdentity.SetupLock.WaitAsync();
        try
        {
            if (await db.Operators.AnyAsync()) return NotFound();
            if (!ModelState.IsValid) return Page();
            var user = new Operator { Name = Input.Name.Trim(), Email = Input.Email.Trim().ToLowerInvariant() };
            user.PasswordHash = WorkspaceIdentity.Hasher.HashPassword(user, Input.Password);
            db.Operators.Add(user);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return NotFound(); }
            if (Input.LoadDemo && config.GetValue<bool>("SeedDemo") && environment.IsDevelopment()) await DemoData.SeedAsync(db, user.Id);
            await WorkspaceIdentity.SignInAsync(HttpContext, user);
            return RedirectToPage("/Index");
        }
        finally { WorkspaceIdentity.SetupLock.Release(); }
    }
}
