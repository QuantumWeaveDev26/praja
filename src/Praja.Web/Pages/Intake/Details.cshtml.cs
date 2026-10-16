using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages.Intake;
public class DetailsModel(InboxService inbox):PageModel
{
    public InboxMessage Item{get;private set;}=null!;
    public List<InboxDuplicate> Duplicates{get;private set;}=[];
    [BindProperty]public int Version{get;set;}
    [BindProperty,StringLength(500,MinimumLength=20)]public string? ReviewNote{get;set;}
    private async Task<bool>Load(Guid id){
        Item=(await inbox.FindAsync(id,User.OwnerId(),HttpContext.RequestAborted))!;if(Item is null)return false;
        Duplicates=await inbox.DuplicatesAsync(Item,HttpContext.RequestAborted);return true;
    }
    public async Task<IActionResult>OnGetAsync(Guid id){if(!await Load(id))return NotFound();Version=Item.Version;return Page();}
    public async Task<IActionResult>OnPostDismissAsync(Guid id){
        if(!await Load(id))return NotFound();if(!ModelState.IsValid)return Page();
        try{await inbox.DismissAsync(id,User.OwnerId(),Version,ReviewNote??"",User.Identity!.Name!,HttpContext.RequestAborted);return RedirectToPage(new{id});}
        catch(InvalidOperationException ex){ModelState.AddModelError("",ex.Message);await Load(id);return Page();}
    }
}
