using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Data;
using Praja.Web.Services;
namespace Praja.Web.Pages;
[EnableRateLimiting("citizen")]
[RequestSizeLimit(18*1024*1024)]
[RequestFormLimits(MultipartBodyLengthLimit=18*1024*1024)]
public class SubmitModel(PublicComplaintService submissions, MediaIntakeService media):PageModel
{
    [BindProperty]public Guid SubmissionKey {get;set;}=Guid.NewGuid();
    [BindProperty]public string Original {get;set;}="";
    [BindProperty]public string District {get;set;}="";
    [BindProperty]public string Location {get;set;}="";
    [BindProperty]public string Language {get;set;}="English";
    [BindProperty]public bool Consent {get;set;}
    [BindProperty]public bool Sample {get;set;}
    [BindProperty]public IFormFile? Photo {get;set;}
    [BindProperty]public bool PhotoConsent {get;set;}
    [BindProperty]public bool PublicMapConsent {get;set;}
    [BindProperty]public double? Latitude {get;set;}
    [BindProperty]public double? Longitude {get;set;}
    [BindProperty]public double? Accuracy {get;set;}
    public bool SpeechConfigured => media.SpeechConfigured;
    [BindProperty]public string? Source {get;set;}="Text";
    public Grievance? Submitted {get;private set;}
    public string? Code {get;private set;}
    public async Task<IActionResult> OnPostAsync()
    {
        if(!ModelState.IsValid){ExplainPhotoReselection();return Page();}
        try{(Submitted,Code)=await submissions.SubmitAsync(SubmissionKey,Original,District,Location,Language,Consent,Sample,Source??"Text",Photo,Latitude,Longitude,Accuracy,PhotoConsent,PublicMapConsent);return Page();}
        catch(InvalidOperationException ex){ModelState.AddModelError("",ex.Message);ExplainPhotoReselection();return Page();}
    }
    private void ExplainPhotoReselection(){if(Photo is not null)ModelState.AddModelError("","Choose your incident photo again before retrying; browsers cannot restore file selections.");}
}
