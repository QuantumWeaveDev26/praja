using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;

public sealed record BriefSource(string Id, string Label, string Text, bool MustInclude = false);
public sealed record BriefPoint(string SourceId, string Quote);
public sealed record CaseBrief(IReadOnlyList<BriefPoint> Points, string Method);
public sealed record CaseBriefSnapshot(Grievance Case, IReadOnlyList<BriefSource> Sources, CaseBrief Brief, IReadOnlyList<string> Notes);

public class CaseBriefService(WorkspaceDb db, DraftingService drafts, EvidenceService evidence, MediaIntakeService media)
{
    public async Task<CaseBriefSnapshot> BuildAsync(Guid id, Guid owner, int version, IReadOnlyList<Guid> selected,
        bool useAi, bool consent, CancellationToken cancellation = default)
    {
        var item = await db.Grievances.AsNoTracking().Include(g => g.History)
            .SingleOrDefaultAsync(g => g.Id == id && g.OwnerId == owner, cancellation) ?? throw new KeyNotFoundException();
        if (item.Version != version) throw new InvalidOperationException("This case changed. Reload the briefing page before generating it.");
        if ((useAi || selected.Count > 0) && await db.InboxMessages.AnyAsync(m => m.OwnerId == owner && m.GrievanceId == id && PublicSourceCollection.LocalReviewActors.Contains(m.CapturedBy), cancellation))
            throw new InvalidOperationException("Public-source cases currently support local text briefings only. Leave AI and supporting-file processing unselected.");
        if ((useAi || selected.Count > 0) && !consent) throw new InvalidOperationException("Confirm processing permission before using AI or reading selected supporting files.");
        if (selected.Count > 2 || selected.Distinct().Count() != selected.Count)
            throw new InvalidOperationException("Select at most two different supporting files for one briefing.");
        var files = await evidence.ListAsync(id, owner, cancellation);
        if (selected.Any(key => files.All(file => file.Id != key))) throw new InvalidOperationException("The selected supporting files are unavailable for this case. Reload before processing.");
        var sources = new List<BriefSource> { new("original", "Citizen’s original statement", item.OriginalText, true) };
        if (item.ResolutionReport.Length > 0) sources.Add(new("resolution", "Department’s recorded resolution report", item.ResolutionReport, true));
        sources.Add(new("feedback", "Citizen feedback recorded by officer", item.CitizenFeedback));
        var notes = new List<string>();
        foreach (var key in selected)
        {
            var file = files.Single(f => f.Id == key);
            if (!useAi && !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            { notes.Add(file.FileName + ": image text was not read. Enable consented AI processing for image OCR, or review the image manually."); continue; }
            var content = await evidence.DownloadAsync(id, key, owner, cancellation) ?? throw new InvalidOperationException("A selected supporting file is no longer available.");
            try
            {
                using var stream = new MemoryStream(content.Bytes);
                var formFile = new FormFile(stream, 0, stream.Length, "document", content.FileName);
                var extracted = await media.ExtractAsync(formFile, item.Language, true, cancellation);
                sources.Add(new("document-" + key.ToString("N"), "Supporting document: " + file.FileName, extracted.Text, true));
                notes.Add(file.FileName + ": " + extracted.Method + ". Compare quoted text with the original file.");
            }
            catch (InvalidOperationException ex) { notes.Add(file.FileName + ": " + ex.Message + " Its contents were not included."); }
        }
        foreach (var entry in item.History.OrderByDescending(h => h.AtUtc).ThenByDescending(h => h.Id).Take(30))
            sources.Add(new("event-" + entry.Id, "Case activity · " + entry.AtUtc.ToString("dd MMM yyyy, HH:mm") + " UTC", entry.Action));
        if (item.History.Count > 30) notes.Add("Only the latest 30 activity entries were considered. Review the complete case history for older events.");
        notes.Add("Evidence references and file authenticity are not verified by this briefing. A source quote proves text provenance, not that the issue was resolved.");
        var brief = await drafts.BriefAsync(sources, useAi, consent, cancellation);
        // Do not show an apparently current summary if another request updated the case.
        if (!await db.Grievances.AsNoTracking().AnyAsync(g => g.Id == id && g.OwnerId == owner && g.Version == version, cancellation))
            throw new InvalidOperationException("This case changed while the briefing was being prepared. Reload and generate it again.");
        return new(item, sources, brief, notes);
    }

    public static CaseBrief Local(IReadOnlyList<BriefSource> sources, string method = "Local source excerpts")
    {
        var ordered = sources.Where(s => s.MustInclude).Concat(sources.Where(s => !s.MustInclude)).Take(12);
        return new(ordered.Select(s => new BriefPoint(s.Id, Excerpt(s.Text, 300))).ToList(), method);
    }
    private static string Excerpt(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var length = limit;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
}
