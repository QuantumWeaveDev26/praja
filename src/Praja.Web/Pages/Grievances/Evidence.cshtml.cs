using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace Praja.Web.Pages.Grievances;

[RequestSizeLimit(10*1024*1024)]
[RequestFormLimits(MultipartBodyLengthLimit=10*1024*1024)]
[EnableRateLimiting("evidence")]
public class EvidenceModel(EvidenceService evidence) : PageModel
{
    [BindProperty] public IFormFile? EvidenceFile {get;set;}
    [BindProperty] public string? Description {get;set;}
    [BindProperty] public bool EvidenceConsent {get;set;}
    [BindProperty] public int Version {get;set;}
    public async Task<IActionResult> OnGetAsync(Guid id,Guid attachmentId)
    {
        try
        {
            var file=await evidence.DownloadAsync(id,attachmentId,User.OwnerId(),HttpContext.RequestAborted);
            if(file is null)return NotFound();
            Response.Headers["Content-Security-Policy"]="sandbox; default-src 'none'";
            return File(file.Bytes,"application/octet-stream",file.FileName);
        }
        catch(InvalidOperationException ex){return BadRequest(ex.Message);}
    }
    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        try
        {
            if(EvidenceFile is null)throw new InvalidOperationException("Choose a PDF, PNG or JPEG supporting file.");
            await evidence.UploadAsync(id,User.OwnerId(),User.Identity!.Name!,Version,EvidenceFile,Description,EvidenceConsent,HttpContext.RequestAborted);
            TempData["EvidenceNotice"]="Supporting file saved with an integrity hash. Review its relevance and authenticity before closure.";
        }
        catch(KeyNotFoundException){return NotFound();}
        catch(InvalidOperationException ex){TempData["EvidenceError"]=ex.Message;}
        return RedirectToPage("Details",new {id});
    }
}
