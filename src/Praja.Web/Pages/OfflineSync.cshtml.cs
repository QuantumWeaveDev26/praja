using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;
namespace Praja.Web.Pages;
[EnableRateLimiting("citizen")]
[RequestSizeLimit(65536)]
public class OfflineSyncModel(PublicComplaintService submissions,IAntiforgery antiforgery):PageModel
{
    public IActionResult OnGet()=>new JsonResult(new{token=antiforgery.GetAndStoreTokens(HttpContext).RequestToken});
    public async Task<IActionResult> OnPostAsync(Guid submissionKey,string original,string district,string location,string language,bool consent,bool sample)
    {
        if(!ModelState.IsValid)return BadRequest(new{error="Check the complaint fields before retrying."});
        try{
            var result=await submissions.SubmitAsync(submissionKey,original??"",district??"",location??"",language??"",consent,sample);
            return new JsonResult(new{reference=result.Case.Reference,code=result.Code});
        }catch(InvalidOperationException ex){return BadRequest(new{error=ex.Message});}
    }
}
