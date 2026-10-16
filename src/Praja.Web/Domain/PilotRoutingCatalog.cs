using System.Text.RegularExpressions;
namespace Praja.Web.Domain;

public sealed record PilotCategory(string Key, string Department, string Label);
public sealed record PilotIssue(string Key, string Category, string Label, string[] Keywords);

// Demonstration taxonomy, not official government departments or category codes.
public static class PilotRoutingCatalog
{
    public static readonly Dictionary<string, string> Services = new()
    {
        ["water"]="Water supply", ["roads"]="Roads & public works", ["electricity"]="Electricity", ["sanitation"]="Sanitation",
        ["revenue"]="Revenue services", ["welfare"]="Social welfare", ["health"]="Public health services", ["education"]="School services",
        ["transport"]="Public transport", ["agriculture"]="Agriculture support", ["review"]="Manual routing review"
    };
    private static readonly Dictionary<string, string[]> ServiceKeywords = new()
    {
        ["water"]=["water","dry taps","taps have been dry","ನೀರು","ನೀರಿನ","ನೀರಿಲ್ಲ"],
        ["roads"]=["road","roads","pothole","potholes","ರಸ್ತೆ","ಗುಂಡಿ"],
        ["electricity"]=["electricity","power cut","power cuts","voltage","ವಿದ್ಯುತ್"],
        ["sanitation"]=["garbage","waste","drain","drainage","ಕಸ","ಚರಂಡಿ"],
        ["revenue"]=["land record","property tax","certificate","ಭೂಮಿ","ಪ್ರಮಾಣಪತ್ರ"],
        ["welfare"]=["pension","scholarship","ration","ಪಿಂಚಣಿ","ಪಡಿತರ","ವಿದ್ಯಾರ್ಥಿವೇತನ"],
        ["health"]=["hospital","clinic","medicine","medicines","ಆಸ್ಪತ್ರೆ","ಔಷಧ"],
        ["education"]=["school","schools","teacher","teachers","ಶಾಲೆ","ಶಿಕ್ಷಕ"],
        ["transport"]=["bus","buses","ಬಸ್","ಬಸ್ಸು"],
        ["agriculture"]=["agriculture","crop","irrigation","ಕೃಷಿ","ಬೆಳೆ","ನೀರಾವರಿ"]
    };
    public static readonly PilotCategory[] Categories =
    [
        new("water_supply","water","Supply and pressure"), new("water_quality","water","Water quality"),
        new("road_condition","roads","Road condition"), new("power_supply","electricity","Electricity supply"),
        new("waste_collection","sanitation","Waste collection"), new("drainage","sanitation","Drainage"),
        new("documents","revenue","Records and certificates"), new("benefits","welfare","Benefit delivery"),
        new("healthcare_access","health","Access to health services"), new("school_facilities","education","School staffing and facilities"),
        new("bus_services","transport","Bus services"), new("farm_support","agriculture","Farm assistance")
    ];
    public static readonly PilotIssue[] Issues =
    [
        new("no_supply","water_supply","No water supply",["no water","water supply stopped","supply has stopped","dry taps","taps have been dry","ನೀರಿಲ್ಲ","ನೀರು ಇಲ್ಲ"]),
        new("low_pressure","water_supply","Low water pressure",["low pressure","ನೀರಿನ ಒತ್ತಡ ಕಡಿಮೆ"]),
        new("contamination","water_quality","Reported water contamination",["dirty water","contaminated water","ಕಲುಷಿತ ನೀರು"]),
        new("potholes","road_condition","Potholes",["pothole","potholes","ಗುಂಡಿ"]),
        new("damaged_surface","road_condition","Damaged road surface",["road is damaged","damaged road","ರಸ್ತೆ ಹಾಳಾಗಿದೆ"]),
        new("power_outage","power_supply","Power interruption",["power cut","power cuts","no electricity","ವಿದ್ಯುತ್ ಇಲ್ಲ"]),
        new("low_voltage","power_supply","Low voltage",["low voltage","ಕಡಿಮೆ ವೋಲ್ಟೇಜ್"]),
        new("uncollected_waste","waste_collection","Uncollected waste",["garbage not collected","uncollected garbage","ಕಸ ಸಂಗ್ರಹವಾಗಿಲ್ಲ"]),
        new("waste_dumping","waste_collection","Waste dumping",["waste dumping","garbage dumped","ಕಸ ಸುರಿಯುತ್ತಿದ್ದಾರೆ"]),
        new("blocked_drain","drainage","Blocked drain",["blocked drain","drain is blocked","ಚರಂಡಿ ಕಟ್ಟಿಕೊಂಡಿದೆ"]),
        new("certificate_delay","documents","Certificate delay",["certificate delay","certificate is delayed","ಪ್ರಮಾಣಪತ್ರ ಬಂದಿಲ್ಲ"]),
        new("land_record","documents","Land record issue",["land record","ಭೂಮಿ ದಾಖಲೆ"]),
        new("pension_delay","benefits","Pension delivery",["pension","ಪಿಂಚಣಿ"]),
        new("scholarship","benefits","Scholarship delivery",["scholarship","ವಿದ್ಯಾರ್ಥಿವೇತನ"]),
        new("ration","benefits","Ration delivery",["ration","ಪಡಿತರ"]),
        new("medicine_shortage","healthcare_access","Medicine availability",["no medicines","medicine shortage","ಔಷಧಿ ಇಲ್ಲ"]),
        new("appointment","healthcare_access","Appointment access",["hospital appointment","clinic appointment","ವೈದ್ಯರ ಭೇಟಿ"]),
        new("teacher_shortage","school_facilities","Teacher availability",["no teachers","teacher shortage","ಶಿಕ್ಷಕರಿಲ್ಲ"]),
        new("school_building","school_facilities","School building",["school building","ಶಾಲಾ ಕಟ್ಟಡ"]),
        new("missed_bus","bus_services","Scheduled bus unavailable",["bus did not arrive","bus cancelled","ಬಸ್ ಬಂದಿಲ್ಲ"]),
        new("bus_stop","bus_services","Bus stop facilities",["bus stop","ಬಸ್ ನಿಲ್ದಾಣ"]),
        new("crop_assistance","farm_support","Crop assistance",["crop compensation","crop assistance","ಬೆಳೆ ಪರಿಹಾರ"]),
        new("irrigation","farm_support","Irrigation assistance",["irrigation","ನೀರಾವರಿ"])
    ];
    public static string? Quote(string original,string keyword)
    {
        // English boundaries prevent road/broadband and bus/business matches.
        // Kannada roots intentionally match inflections; they remain keyword hints.
        var pattern=keyword.All(c=>c<=127)?@"(?<![\p{L}\p{N}])"+Regex.Escape(keyword)+@"(?![\p{L}\p{N}])":Regex.Escape(keyword);
        var match=Regex.Match(original,pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        return match.Success?match.Value:null;
    }
    public static IReadOnlyList<string> MatchingServices(string original)=>ServiceKeywords.Where(s=>s.Value.Any(word=>Quote(original,word)is not null)).Select(s=>s.Key).ToArray();
    public static string ServiceEvidence(string original,string department)=>ServiceKeywords.TryGetValue(department,out var words)?words.Select(word=>Quote(original,word)).FirstOrDefault(q=>q is not null)??"":"";
    public static (string Category,string Subcategory,string Evidence) SuggestIssue(string original,string department)
    {
        var matches=Issues.Where(i=>Categories.Any(c=>c.Key==i.Category&&c.Department==department))
            .Select(i=>(Issue:i,Quote:i.Keywords.Select(word=>Quote(original,word)).FirstOrDefault(q=>q is not null))).Where(m=>m.Quote is not null).ToArray();
        var categories=matches.Select(m=>m.Issue.Category).Distinct().ToArray();
        return(categories.Length==1?categories[0]:"",matches.Length==1?matches[0].Issue.Key:"",matches.FirstOrDefault().Quote??ServiceEvidence(original,department));
    }
    public static bool ValidSelection(string department,string category,string issue)=>category.Length==0?issue.Length==0:
        Categories.Any(c=>c.Key==category&&c.Department==department)&&(issue.Length==0||Issues.Any(i=>i.Key==issue&&i.Category==category));
    public static string CategoryLabel(string key)=>Categories.FirstOrDefault(c=>c.Key==key)?.Label??"Not classified";
    public static string IssueLabel(string key)=>Issues.FirstOrDefault(i=>i.Key==key)?.Label??"Not classified";
}
