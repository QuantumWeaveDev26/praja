using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;
using Praja.Web.Data;

namespace Praja.Web.Pages.Grievances;
[RequestSizeLimit(10 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
[EnableRateLimiting("intake")]
public class NewModel(CaseService cases, DraftingService drafting, MediaIntakeService media, IntakeReviewService reviews, InboxService inbox) : PageModel
{
    [BindProperty] public IntakeInput Input { get; set; } = new();
    [BindProperty] public string? CaptureMethod { get; set; } = "Text";
    [BindProperty, StringLength(16000)] public string? AnalysisToken { get; set; }
    public IntakeAnalysis? Analysis { get; private set; }
    [BindProperty(SupportsGet=true)] public Guid? InboxId {get;set;}
    [BindProperty] public int InboxVersion {get;set;}
    public InboxMessage? InboxItem {get;private set;}
    public async Task<IActionResult>OnGetAsync()
    {
        if(InboxId is not { } id)return Page();
        InboxItem=await inbox.FindAsync(id,User.OwnerId(),HttpContext.RequestAborted);
        if(InboxItem is null)return NotFound();
        if(InboxItem.GrievanceId is { } caseId)return RedirectToPage("Details",new{id=caseId});
        if(InboxItem.State!="Pending")return BadRequest("This message is no longer pending.");
        InboxVersion=InboxItem.Version;Input.OriginalText=InboxItem.OriginalText;Input.Source=InboxItem.Channel;Input.Language=InboxItem.Language;return Page();
    }
    private async Task CheckInbox()
    {
        if(InboxId is not { } id)return;
        InboxItem=await inbox.FindAsync(id,User.OwnerId(),HttpContext.RequestAborted)??throw new InvalidOperationException("Inbox item not found.");
        if(InboxItem.State!="Pending"||InboxItem.Version!=InboxVersion)throw new InvalidOperationException("This inbox item changed or was already processed. Reload it.");
        if(Input.OriginalText?.Trim()!=InboxItem.OriginalText)throw new InvalidOperationException("The inbox original is preserved. Edit the formal draft on the next screen.");
        Input.Source=InboxItem.Channel;
    }
    public bool AiConnected => InboxItem?.CapturedBy != "X public search" && drafting.IsConnected;
    public bool OcrConfigured => media.OcrConfigured;
    public bool SpeechConfigured => media.SpeechConfigured;
    public string? IntakeNotice { get; private set; }
    [BindProperty] public IFormFile? Document { get; set; }
    [BindProperty] public IFormFile? Audio { get; set; }
    public class IntakeInput
    {
        [Required, StringLength(6000, MinimumLength = 20)] public string OriginalText { get; set; } = "";
        [Required, StringLength(100, MinimumLength = 2)] public string District { get; set; } = "";
        [Required, StringLength(200, MinimumLength = 2)] public string Location { get; set; } = "";
        public string Language { get; set; } = "English";
        public string Source { get; set; } = "Text";
        public bool Consent { get; set; }
        [StringLength(120)] public string? FollowUpTimeline { get; set; }
        [StringLength(500)] public string? FollowUpHelp { get; set; }
        public bool FollowUpConfirmed { get; set; }
    }
    public Task<IActionResult> OnPostDocumentAsync() => Extract(false);
    public Task<IActionResult> OnPostAudioAsync() => Extract(true);
    public async Task<IActionResult> OnPostAnalyzeAsync()
    {
        ModelState.Clear(); AnalysisToken = "";
        try
        {
            await CheckInbox();
            if (Input.Language is not ("English" or "Kannada") || !SupportedSource(Input.Source))
                throw new InvalidOperationException("Choose a supported draft language and intake channel.");
            if ((Input.District?.Length ?? 0) > 100 || (Input.Location?.Length ?? 0) > 200)
                throw new InvalidOperationException("Keep district and location within the displayed limits.");
            if ((Input.FollowUpTimeline?.Length ?? 0) > 120 || (Input.FollowUpHelp?.Length ?? 0) > 500)
                throw new InvalidOperationException("Keep follow-up answers within the displayed limits.");
            if (!Input.Consent) throw new InvalidOperationException("Confirm consent before suggesting details.");
            Analysis = InboxItem?.CapturedBy == "X public search"
                ? AutomationService.WithLocalQuotes(Input.OriginalText, DraftingService.LocalAnalysis(Input.OriginalText, "Local keyword routing · X source"))
                : await drafting.AnalyzeAsync(Input.OriginalText, Input.Consent, HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(Input.District)) Input.District = Analysis.District;
            if (string.IsNullOrWhiteSpace(Input.Location)) Input.Location = Analysis.Location;
            AnalysisToken = reviews.Protect(User.OwnerId(), Input.OriginalText, Analysis);
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); }
        return Page();
    }
    public IActionResult OnPostClearAnalysis()
    {
        ModelState.Clear(); AnalysisToken = "";
        return Page();
    }
    private static bool SupportedSource(string source) => InboxService.Channels.Contains(source);
    private async Task<IActionResult> Extract(bool audio)
    {
        ModelState.Clear();
        CaptureMethod = audio ? "Voice" : "Document";
        AnalysisToken = "";
        if(InboxId.HasValue){ModelState.AddModelError("","The incoming source is preserved. Review its formal draft rather than replace the source with another file.");return Page();}
        var file = audio ? Audio : Document;
        var json = audio && Request.Headers.Accept.ToString().Split(',').Any(value => value.Trim().StartsWith("application/json", StringComparison.OrdinalIgnoreCase));
        IActionResult Failure(string message)
        {
            ModelState.AddModelError("", message);
            return json ? new JsonResult(new { error = message }) { StatusCode = 400 } : Page();
        }
        if (file is null) return Failure(audio ? "Record and stop the audio, or choose an audio file first." : "Choose a petition file first. For a recording, use Transcribe audio.");
        try
        {
            var result = audio
                ? await media.TranscribeAsync(file, Input.Language, Input.Consent, HttpContext.RequestAborted)
                : await media.ExtractAsync(file, Input.Language, Input.Consent, HttpContext.RequestAborted);
            Input.OriginalText = result.Text;
            Input.FollowUpConfirmed = false;
            Input.Source = audio ? "Voice transcript" : "Document extraction";
            IntakeNotice = result.Method + ". Check the text against the original, correct any errors, then prepare the draft. The file itself is not stored with the case.";
            if (json) return new JsonResult(new { text = result.Text, method = result.Method });
        }
        catch (InvalidOperationException ex) { return Failure(ex.Message); }
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await CheckInbox();
            if (!string.IsNullOrEmpty(AnalysisToken)) Analysis = reviews.Read(AnalysisToken, User.OwnerId(), Input.OriginalText);
            if (!ModelState.IsValid) return Page();
            var followUp = new FollowUpAnswers(Input.FollowUpTimeline, Input.FollowUpHelp, Input.FollowUpConfirmed);
            var item = InboxId is { } id
                ? await cases.CreateFromInboxAsync(id,InboxVersion,User.OwnerId(),User.Identity!.Name!,Input.OriginalText,Input.Language,Input.District,Input.Location,Input.Consent,HttpContext.RequestAborted,Analysis?.Route,followUp)
                : await cases.CreateAsync(User.OwnerId(), User.Identity!.Name!, Input.OriginalText, Input.Language, Input.District, Input.Location, Input.Source, Input.Consent, HttpContext.RequestAborted, Analysis?.Route,followUp);
            return RedirectToPage("Details", new { id = item.Id });
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); return Page(); }
    }
}
