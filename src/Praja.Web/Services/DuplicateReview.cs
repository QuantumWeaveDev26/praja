using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;

public partial class InboxService
{
    // Local overlap hints are explainable and require review; they are not semantic certainty.
    public static string? MatchReason(string left,string right)
    {
        if(Fingerprint(left)==Fingerprint(right))return "Exact normalized wording";
        static HashSet<string> Tokens(string value)=>Regex.Matches(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),@"[\p{L}\p{M}\p{N}]+",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
            .Select(m=>m.Value).Where(t=>t.Length>1&&!StopWords.Contains(t)).ToHashSet(StringComparer.Ordinal);
        var a=Tokens(left);var b=Tokens(right);
        var numbersA=Regex.Matches(left,@"\p{N}+",RegexOptions.None,TimeSpan.FromMilliseconds(100)).Select(m=>m.Value).ToHashSet();
        var numbersB=Regex.Matches(right,@"\p{N}+",RegexOptions.None,TimeSpan.FromMilliseconds(100)).Select(m=>m.Value).ToHashSet();
        if(!numbersA.SetEquals(numbersB))return null;
        var shared=a.Intersect(b).Count();
        return shared>=5&&2.0*shared/(a.Count+b.Count)>=0.72 ? "Similar wording; compare location, dates and incident before linking" : null;
    }
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {"the","and","for","this","that","with","from","have","has","been","are","was","were","please","kindly","our","your","complaint","subject","request"};

    public async Task ReviewDuplicateAsync(Guid id,Guid owner,int version,Guid? targetCaseId,string note,string actor,CancellationToken cancellation=default)
    {
        note=note.Trim();
        if(note.Length is <20 or >500)throw new InvalidOperationException("Record a review reason of 20–500 characters.");
        var item=await FindAsync(id,owner,cancellation)??throw new InvalidOperationException("Inbox item not found.");
        if(item.State!="Pending"||item.Version!=version)throw new InvalidOperationException("This inbox item changed. Reload before reviewing it.");
        if(targetCaseId is {} targetId)
        {
            var target=await db.Grievances.SingleOrDefaultAsync(g=>g.Id==targetId&&g.OwnerId==owner,cancellation)
                ??throw new InvalidOperationException("Matching case not found in this workspace.");
            if(target.Status=="Draft"||target.IsSample||MatchReason(item.OriginalText,target.OriginalText) is null)
                throw new InvalidOperationException("Select a matching registered case; draft or sample cases cannot receive duplicate links.");
            item.State="Duplicate";item.DuplicateOfId=target.Id;
            target.History.Add(new(){Actor=actor,Action=$"Officer linked inbox message {item.Id} as a duplicate. Original source and review reason remain in intake."});
            // Concurrency-check the case as well as the source when recording the decision.
            target.Version++;target.UpdatedUtc=DateTime.UtcNow;
        }
        item.ReviewNote=note;item.ReviewedBy=actor;item.ReviewedUtc=DateTime.UtcNow;item.Version++;
        try{await db.SaveChangesAsync(cancellation);}
        catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();throw new InvalidOperationException("The source or case changed. Reload before reviewing it.");}
    }
}
