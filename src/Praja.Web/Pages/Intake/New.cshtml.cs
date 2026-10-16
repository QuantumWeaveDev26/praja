using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
namespace Praja.Web.Pages.Intake;
[RequestSizeLimit(128*1024)]
[EnableRateLimiting("inbox")]
public class NewModel(InboxService inbox) : PageModel
{
    [BindProperty]public CaptureInput Input {get;set;}=new();
    public class CaptureInput {
        [Required,StringLength(6000,MinimumLength=20)]public string OriginalText{get;set;}="";
        public string Channel{get;set;}="Text";
        public string Language{get;set;}="English";
        [StringLength(1000)]public string? SourceUrl{get;set;}
        [StringLength(150)]public string? ExternalId{get;set;}
        public bool Permission{get;set;}
    }
    public async Task<IActionResult>OnPostAsync(){
        if(!ModelState.IsValid)return Page();
        try{
            var item=await inbox.CaptureAsync(User.OwnerId(),User.Identity!.Name!,Input.OriginalText,Input.Channel,Input.Language,Input.SourceUrl,Input.ExternalId,Input.Permission,HttpContext.RequestAborted);
            return RedirectToPage("Details",new{id=item.Id});
        }catch(InvalidOperationException ex){ModelState.AddModelError("",ex.Message);return Page();}
    }
}
