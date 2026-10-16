using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Domain;
using Praja.Web.Services;

namespace Praja.Web.Pages;

[EnableRateLimiting("citizen")]
public class CitizenModel(CitizenAccessService access) : PageModel
{
    [BindProperty] public string Language { get; set; } = "English";
    [BindProperty] public string Topic { get; set; } = "filing";
    [BindProperty] public string? Concern { get; set; }
    // The access code is read only from the POST form and is never rebound into a response.
    public string? AccessCode { get; set; }
    public string? Guidance { get; private set; }
    public string? SuggestedService { get; private set; }
    public string? TrackingError { get; private set; }
    public CitizenCaseStatus? TrackedCase { get; private set; }
    public bool Kannada => Language == "Kannada";
    public string Text(string english, string kannada) => Kannada ? kannada : english;
    public void OnGet() { }

    public IActionResult OnPostGuide()
    {
        if (Language is not ("English" or "Kannada") || Topic is not ("filing" or "category" or "status") || Concern?.Length > 1000)
        {
            Language = Language == "Kannada" ? "Kannada" : "English";
            ModelState.AddModelError("", Text("Choose a supported language and question. Keep the concern within 1,000 characters.", "ಲಭ್ಯವಿರುವ ಭಾಷೆ ಮತ್ತು ಪ್ರಶ್ನೆಯನ್ನು ಆಯ್ಕೆಮಾಡಿ. ಸಮಸ್ಯೆಯನ್ನು 1,000 ಅಕ್ಷರಗಳೊಳಗೆ ಬರೆಯಿರಿ."));
            return Page();
        }
        if (Topic == "filing") Guidance = Text(
            "Prepare what happened, the district and locality, when it started, and the help you need. Ask the pilot workspace officer to record the concern with your consent, review the draft and service route, then register it. This page does not submit a complaint to the government or create a case.",
            "ಏನಾಯಿತು, ಜಿಲ್ಲೆ ಮತ್ತು ಸ್ಥಳ, ಸಮಸ್ಯೆ ಯಾವಾಗ ಆರಂಭವಾಯಿತು ಮತ್ತು ಬೇಕಾದ ಸಹಾಯವನ್ನು ಸಿದ್ಧಪಡಿಸಿ. ನಿಮ್ಮ ಒಪ್ಪಿಗೆಯೊಂದಿಗೆ ಸಮಸ್ಯೆಯನ್ನು ದಾಖಲಿಸಲು ಪೈಲಟ್ ಕಾರ್ಯಸ್ಥಳದ ಅಧಿಕಾರಿಯನ್ನು ಕೇಳಿ. ಅಧಿಕಾರಿ ಕರಡು ಮತ್ತು ಸೇವಾ ವಿಭಾಗವನ್ನು ಪರಿಶೀಲಿಸಿ ನಂತರ ನೋಂದಣಿ ಮಾಡುತ್ತಾರೆ. ಈ ಪುಟ ಸರ್ಕಾರಕ್ಕೆ ದೂರು ಸಲ್ಲಿಸುವುದಿಲ್ಲ ಅಥವಾ ಪ್ರಕರಣ ರಚಿಸುವುದಿಲ್ಲ.");
        else if (Topic == "status") Guidance = Text(
            "After local registration, ask the officer for a private access code. Paste it into the status form below. A code expires within seven days; request a new one if it is unavailable. Keep it private: anyone with the code can see the limited status summary. A local status does not confirm an IPGRS submission or government action.",
            "ಸ್ಥಳೀಯ ನೋಂದಣಿಯ ನಂತರ ಅಧಿಕಾರಿಯಿಂದ ಖಾಸಗಿ ಪ್ರವೇಶ ಕೋಡ್ ಪಡೆಯಿರಿ. ಅದನ್ನು ಕೆಳಗಿನ ಸ್ಥಿತಿ ಅರ್ಜಿಯಲ್ಲಿ ಹಾಕಿ. ಕೋಡ್ ಏಳು ದಿನಗಳೊಳಗೆ ಅವಧಿ ಮುಗಿಯುತ್ತದೆ; ಅಗತ್ಯವಿದ್ದರೆ ಹೊಸ ಕೋಡ್ ಕೇಳಿ. ಕೋಡ್ ಖಾಸಗಿಯಾಗಿ ಇರಲಿ: ಅದನ್ನು ಹೊಂದಿರುವವರು ಸೀಮಿತ ಸ್ಥಿತಿ ವಿವರವನ್ನು ನೋಡಬಹುದು. ಸ್ಥಳೀಯ ಸ್ಥಿತಿ IPGRS ಸಲ್ಲಿಕೆ ಅಥವಾ ಸರ್ಕಾರದ ಕ್ರಮವನ್ನು ದೃಢಪಡಿಸುವುದಿಲ್ಲ.");
        else
        {
            if (string.IsNullOrWhiteSpace(Concern) || Concern.Trim().Length < 3)
            {
                ModelState.AddModelError(nameof(Concern), Text("Add a short description of the service issue.", "ಸೇವೆಯ ಸಮಸ್ಯೆಯ ಬಗ್ಗೆ ಸಂಕ್ಷಿಪ್ತವಾಗಿ ಬರೆಯಿರಿ."));
                return Page();
            }
            var suggestion = CaseRules.SuggestDepartment(Concern);
            SuggestedService = ServiceLabel(suggestion.Department);
            Guidance = suggestion.NeedsManualReview ? Text(
                "The keyword rules cannot choose one pilot service. Ask the officer to review the concern and decide the route. These pilot categories are guidance and are not a verified government department directory.",
                "ಕೀವರ್ಡ್ ನಿಯಮಗಳಿಂದ ಒಂದೇ ಪೈಲಟ್ ಸೇವೆಯನ್ನು ಆಯ್ಕೆಮಾಡಲಾಗಲಿಲ್ಲ. ಸಮಸ್ಯೆಯನ್ನು ಪರಿಶೀಲಿಸಿ ವಿಭಾಗ ನಿರ್ಧರಿಸಲು ಅಧಿಕಾರಿಯನ್ನು ಕೇಳಿ. ಈ ಪೈಲಟ್ ವಿಭಾಗಗಳು ಮಾರ್ಗದರ್ಶನ ಮಾತ್ರ; ಪರಿಶೀಲಿಸಿದ ಸರ್ಕಾರಿ ಇಲಾಖೆಗಳ ಪಟ್ಟಿಯಲ್ಲ.") : Text(
                "This suggestion uses English and Kannada keywords. Ask the officer to confirm the service before registration. These pilot categories are guidance and are not a verified government department directory.",
                "ಈ ಸಲಹೆ ಇಂಗ್ಲಿಷ್ ಮತ್ತು ಕನ್ನಡ ಕೀವರ್ಡ್‌ಗಳನ್ನು ಬಳಸುತ್ತದೆ. ನೋಂದಣಿಯ ಮೊದಲು ಸೇವೆಯನ್ನು ದೃಢಪಡಿಸಲು ಅಧಿಕಾರಿಯನ್ನು ಕೇಳಿ. ಈ ಪೈಲಟ್ ವಿಭಾಗಗಳು ಮಾರ್ಗದರ್ಶನ ಮಾತ್ರ; ಪರಿಶೀಲಿಸಿದ ಸರ್ಕಾರಿ ಇಲಾಖೆಗಳ ಪಟ್ಟಿಯಲ್ಲ.");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostTrackAsync()
    {
        Language = Language == "Kannada" ? "Kannada" : "English";
        // Never accept a bearer code from a query string, including on a POST request.
        var posted = Request.HasFormContentType ? Request.Form["AccessCode"] : default;
        var code = !Request.Query.ContainsKey("AccessCode") && posted.Count == 1 ? posted[0] : null;
        // Tracking does not submit the separate guidance form's fields.
        ModelState.Clear();
        Topic = "filing";
        Concern = null;
        AccessCode = null;
        TrackedCase = await access.GetStatusAsync(code, HttpContext.RequestAborted);
        if (TrackedCase is null) TrackingError = Text(
            "The status is unavailable. Check the access code or ask the officer for a new one.",
            "ಸ್ಥಿತಿ ಲಭ್ಯವಿಲ್ಲ. ಪ್ರವೇಶ ಕೋಡ್ ಪರಿಶೀಲಿಸಿ ಅಥವಾ ಅಧಿಕಾರಿಯಿಂದ ಹೊಸ ಕೋಡ್ ಕೇಳಿ.");
        return Page();
    }

    public string StatusLabel(string status) => !Kannada ? CaseRules.StatusLabel(status) : status switch
    {
        "Registered" => "ನೋಂದಣಿಯಾಗಿದೆ", "InProgress" => "ಕೆಲಸ ಪ್ರಗತಿಯಲ್ಲಿದೆ", "ClosureReview" => "ಮುಕ್ತಾಯ ಪರಿಶೀಲನೆ", "Closed" => "ಮುಕ್ತಾಯವಾಗಿದೆ", _ => "ಪರಿಶೀಲನೆ ಅಗತ್ಯ"
    };
    private string ServiceLabel(string department) => !Kannada ? CaseRules.Departments[department] : department switch
    {
        "water" => "ನೀರಿನ ಸರಬರಾಜು", "roads" => "ರಸ್ತೆ ಮತ್ತು ಸಾರ್ವಜನಿಕ ಕಾಮಗಾರಿ", "electricity" => "ವಿದ್ಯುತ್", "sanitation" => "ಸ್ವಚ್ಛತೆ",
        "revenue" => "ಕಂದಾಯ ಸೇವೆಗಳು", "welfare" => "ಸಮಾಜ ಕಲ್ಯಾಣ", "health" => "ಸಾರ್ವಜನಿಕ ಆರೋಗ್ಯ ಸೇವೆಗಳು", "education" => "ಶಾಲಾ ಸೇವೆಗಳು", "transport" => "ಸಾರ್ವಜನಿಕ ಸಾರಿಗೆ", "agriculture" => "ಕೃಷಿ ಸಹಾಯ", _ => "ಅಧಿಕಾರಿಯ ಪರಿಶೀಲನೆ ಅಗತ್ಯ"
    };
}
