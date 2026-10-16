namespace Praja.Web.Services;

// Citizen answers are officer-confirmed context, never replacements for the source.
public sealed record FollowUpAnswers(string? Timeline, string? RequestedHelp, bool Confirmed)
{
    public string DraftSection(string language)
    {
        var timeline = Timeline?.Trim() ?? "";
        var help = RequestedHelp?.Trim() ?? "";
        if (timeline.Length > 120 || help.Length > 500)
            throw new InvalidOperationException("Keep the follow-up timeline within 120 characters and requested help within 500 characters.");
        if (timeline.Length == 0 && help.Length == 0) return "";
        if (!Confirmed) throw new InvalidOperationException("Confirm the follow-up answers with the citizen before preparing the draft, or clear unknown answers.");
        var lines = new List<string> { language == "Kannada" ? "ನಾಗರಿಕರಿಂದ ಪಡೆದ ಹೆಚ್ಚುವರಿ ಮಾಹಿತಿ (ಅಧಿಕಾರಿ ದಾಖಲಿಸಿದ್ದು):" : "Citizen follow-up (recorded by officer):" };
        if (timeline.Length > 0) lines.Add((language == "Kannada" ? "ಸಮಸ್ಯೆಯ ಸಮಯ: " : "Reported timeline: ") + timeline);
        if (help.Length > 0) lines.Add((language == "Kannada" ? "ಕೋರಿರುವ ಸಹಾಯ: " : "Requested help: ") + help);
        return "\n\n" + string.Join("\n", lines);
    }
}
