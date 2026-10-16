using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;

namespace Praja.Web.Services;

public static class DemoData
{
    public static async Task SeedAsync(WorkspaceDb db, Guid owner)
    {
        if (await db.Grievances.AnyAsync(g => g.OwnerId == owner)) return;
        var samples = new[]
        {
            ("Interrupted water supply in Ward 12", "There has been no drinking water supply in Ward 12 for the past five days. Please restore the water supply.", "Bengaluru Urban", "Ward 12, North zone", "water", "Registered", "English", "Text"),
            ("ರಸ್ತೆಯ ಗುಂಡಿಗಳಿಂದ ಸಂಚಾರಕ್ಕೆ ತೊಂದರೆ", "ಮೈಸೂರಿನ ವಾರ್ಡ್ 8 ರ ಮುಖ್ಯ ರಸ್ತೆಯಲ್ಲಿ ದೊಡ್ಡ ಗುಂಡಿಗಳಿವೆ. ಮಳೆಯ ಸಮಯದಲ್ಲಿ ಜನರಿಗೆ ಸಂಚಾರಕ್ಕೆ ತೊಂದರೆಯಾಗುತ್ತಿದೆ. ದಯವಿಟ್ಟು ರಸ್ತೆಯನ್ನು ದುರಸ್ತಿ ಮಾಡಿ.", "Mysuru", "Ward 8, Main road", "roads", "Draft", "Kannada", "Field meeting"),
            ("Delayed pension application", "My pension application has been pending for two months. Please review the application and let me know which documents are needed.", "Dharwad", "Ward 4", "welfare", "InProgress", "English", "Text"),
            ("Recurring power cuts near the school", "There are frequent electricity cuts near the school in our ward. Children cannot study in the evening. Please inspect the power supply.", "Ballari", "Ward 6, School lane", "electricity", "ClosureReview", "English", "Paper transcription"),
            ("Waste collection restored", "Garbage has not been collected from the market street for a week. Please arrange daily waste collection.", "Mangaluru", "Market street", "sanitation", "Closed", "English", "Text"),
            ("Waterlogging and damaged road", "The road in our ward is damaged and the water supply pipe is leaking. Please inspect both issues and guide us to the right department.", "Bengaluru Rural", "Ward 3", "review", "Draft", "English", "Field meeting")
        };
        var index = 0;
        foreach (var sample in samples)
        {
            var created = DateTime.UtcNow.AddDays(-(++index));
            var item = new Grievance
            {
                OwnerId = owner, Reference = $"DEMO-2026-{index:0000}", Title = sample.Item1, OriginalText = sample.Item2,
                DraftText = sample.Item2, Summary = sample.Item2, District = sample.Item3, Location = sample.Item4,
                Department = sample.Item5, Status = sample.Item6, Language = sample.Item7, Source = sample.Item8,
                IsSample = true, CreatedUtc = created, UpdatedUtc = created.AddHours(2),
                RoutingReason = CaseRules.SuggestDepartment(sample.Item2).Reason, DraftMethod = "Fictional demonstration record"
            };
            if (item.Status == "ClosureReview")
            {
                item.ResolutionReport = "Electricity feeder repairs were completed and power supply was restored after an on-site inspection of the school lane.";
                item.EvidenceReference = "Sample inspection ELE-06";
            }
            if (item.Status == "Closed")
            {
                item.ResolutionReport = "The sanitation team restarted daily garbage collection on Market Street and inspected the waste collection schedule with residents.";
                item.EvidenceReference = "Sample sanitation record SAN-04"; item.CitizenFeedback = "Confirmed"; item.ClosedUtc = created.AddDays(1);
            }
            item.History.Add(new() { Actor = "Demo data", Action = "Fictional case loaded to demonstrate the workspace. No government submission was made.", AtUtc = created });
            db.Grievances.Add(item);
        }
        await db.SaveChangesAsync();
    }
}
