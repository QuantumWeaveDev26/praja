using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;
namespace Praja.Web.Pages;
[EnableRateLimiting("citizen-voice")]
[RequestSizeLimit(9 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 9 * 1024 * 1024)]
public class CitizenVoiceModel(MediaIntakeService media):PageModel
{
    private static readonly SemaphoreSlim Slots = new(2);
    public IActionResult OnGet()=>RedirectToPage("/Submit");
    public async Task<IActionResult> OnPostAudioAsync(IFormFile? audio,string? language,bool voiceConsent)
    {
        if(audio is null||!voiceConsent)return new JsonResult(new{error="Record or choose audio and agree to speech processing first."}){StatusCode=400};
        if(!await Slots.WaitAsync(0,HttpContext.RequestAborted))return new JsonResult(new{error="Speech service is busy. Your audio stays on this page; try again shortly."}){StatusCode=429};
        try
        {
            var result=await media.TranscribeAsync(audio,language??"",voiceConsent,HttpContext.RequestAborted);
            return new JsonResult(new{text=result.Text,method=result.Method});
        }
        catch(InvalidOperationException ex){return new JsonResult(new{error=ex.Message}){StatusCode=400};}
        finally{Slots.Release();}
    }
}
