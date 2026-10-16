namespace Praja.Web.Domain;

public record RoutingSuggestion(string Department, string Reason, bool NeedsManualReview)
{
    public string Category { get; init; } = "";
    public string Subcategory { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Method { get; init; } = "";
    public IReadOnlyList<string> Candidates { get; init; } = [];
}
public record ClosureAssessment(IReadOnlyList<string> Flags)
{
    public bool CanClose => Flags.Count == 0;
}

public static class CaseRules
{
    public static readonly Dictionary<string, string> Departments = PilotRoutingCatalog.Services;
    public static readonly string[] Statuses = ["Draft", "Registered", "InProgress", "ClosureReview", "AwaitingCitizen", "Closed"];
    public static string StatusLabel(string status) => status switch { "InProgress" => "In progress", "ClosureReview" => "Closure review", "AwaitingCitizen" => "Awaiting citizen", _ => status };
    public static string WorkflowLabel(string stage) => stage switch { "Assigned" => "Assigned to officer", "InProgress" => "In progress", "OfficerReview" => "Awaiting department officer review", "Verification" => "Awaiting reviewer", "CitizenConfirmation" => "Awaiting citizen confirmation", _ => StatusLabel(stage) };
    public static RoutingSuggestion SuggestDepartment(string complaint)
    {
        var matches = PilotRoutingCatalog.MatchingServices(complaint);
        if (matches.Count != 1) return new("review", matches.Count > 1 ? "Multiple service keyword hints: " + string.Join(", ", matches.Select(key => Departments[key])) + ". An officer must choose the route." : "No clear service match. An officer must choose the route.", true)
            { Method = "Local keywords", Candidates = matches };
        var issue = PilotRoutingCatalog.SuggestIssue(complaint, matches[0]);
        return new(matches[0], $"Keyword suggestion for {Departments[matches[0]]}. Confirm the service, category and issue before registering.", false)
            { Category = issue.Category, Subcategory = issue.Subcategory, Evidence = issue.Evidence, Method = "Local keywords", Candidates = matches };
    }
    public static ClosureAssessment AssessClosure(string complaint, string report, string evidence, string feedback)
    {
        var flags = new List<string>();
        if (report.Trim().Length < 80) flags.Add("Add a specific resolution report describing the action taken (at least 80 characters).");
        if (string.IsNullOrWhiteSpace(evidence)) flags.Add("An evidence reference is missing. Record an inspection or supporting document reference.");
        if (feedback != "Confirmed") flags.Add(feedback == "Disagreed" ? "The citizen reports that the issue remains unresolved." : "Citizen confirmation has not been recorded.");
        var category = SuggestDepartment(complaint);
        if (category.Department != "review" && PilotRoutingCatalog.ServiceEvidence(report, category.Department).Length == 0)
            flags.Add("The report does not mention the service identified in the original complaint. Review its relevance.");
        return new(flags);
    }
    public static void ValidateTransition(string current, string next, bool reviewed, ClosureAssessment? assessment = null)
    {
        var valid = (current, next) switch
        {
            ("Draft", "Registered") => reviewed,
            ("Registered", "InProgress") => true,
            ("InProgress", "ClosureReview") => true,
            ("ClosureReview", "ClosureReview") => true,
            ("ClosureReview", "InProgress") => true,
            ("ClosureReview", "Closed") => reviewed && assessment?.CanClose == true,
            _ => false
        };
        if (!valid) throw new InvalidOperationException("This action is not allowed at the current stage. Closing requires a completed checklist and explicit officer confirmation.");
    }
}
