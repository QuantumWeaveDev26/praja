using Microsoft.AspNetCore.Mvc.RazorPages;
using Praja.Web.Services;

namespace Praja.Web.Pages;
public class ReadinessModel(DraftingService drafts, IConfiguration config, MediaIntakeService intake, GmailConfigurationStore gmail) : PageModel
{
    public bool AiConnected => drafts.IsConnected;
    public bool OcrConfigured => intake.OcrConfigured;
    public bool SpeechConfigured => intake.SpeechConfigured;
    public bool GmailConnected => gmail.Read(User.OwnerId()).Connected;
    public string Database => config["Database:Provider"] == "SqlServer" ? "SQL Server" : "SQLite · local preview";
}
