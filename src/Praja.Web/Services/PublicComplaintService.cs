using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;
namespace Praja.Web.Services;

public class PublicComplaintService(WorkspaceDb db,WorkflowService workflow,EvidenceService evidence)
{
    public async Task<(Grievance Case,string Code)> SubmitAsync(Guid submissionKey,string original,string district,string location,string language,bool consent,bool sample,string source="Text",IFormFile? photo=null,double? latitude=null,double? longitude=null,double? accuracy=null,bool photoConsent=false,bool publicMapConsent=false)
    {
        if(source is not ("Text" or "Voice transcript"))throw new InvalidOperationException("Choose a supported complaint input method.");
        if(submissionKey==Guid.Empty||!consent||original.Trim().Length is <20 or >6000||district.Trim().Length is <2 or >100||location.Trim().Length is <2 or >200||language is not ("English" or "Kannada"))throw new InvalidOperationException("Enter the complaint, district and locality within the limits, and confirm permission to process it.");
        var prior=await db.CitizenReceipts.AsNoTracking().SingleOrDefaultAsync(r=>r.SubmissionKey==submissionKey);
        if(prior is not null)return(await db.Grievances.AsNoTracking().SingleAsync(g=>g.Id==prior.Id),workflow.ReceiptCode(prior));
        ValidateIncidentMedia(photo,latitude,longitude,accuracy,photoConsent);
        if(publicMapConsent&&!latitude.HasValue)throw new InvalidOperationException("Capture an incident location before sharing on the public map, or clear public map permission.");
        var owner=await db.Operators.AsNoTracking().SingleOrDefaultAsync(o=>o.BootstrapSlot==1)??throw new InvalidOperationException("The workspace is not ready to receive complaints yet.");
        var route=CaseRules.SuggestDepartment(original);
        var item=new Grievance{OwnerId=owner.Id,Reference=$"PRJ-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",Title=original.Trim()[..Math.Min(100,original.Trim().Length)],OriginalText=original.Trim(),DraftText=original.Trim(),Summary=original.Trim()[..Math.Min(250,original.Trim().Length)],District=district.Trim(),Location=location.Trim(),Language=language,Source=source,Status="Registered",Department=route.Department,CategoryCode=route.Category,SubcategoryCode=route.Subcategory,RoutingReason=route.Reason,RoutingEvidence=route.Evidence,RoutingMethod="Local keywords; citizen submission",DraftMethod="Citizen submitted original",IsSample=sample};
        item.History.Add(new(){Actor="Citizen portal",Action="Citizen submitted the original complaint with permission. Local categorisation is provisional; an intake officer reviews uncertain routes."});
        item.PublicMapConsent=publicMapConsent;
        item.IncidentLatitude=latitude;item.IncidentLongitude=longitude;item.IncidentAccuracyMetres=accuracy;
        if(latitude.HasValue)item.History.Add(new(){Actor="Citizen portal",Action="Citizen supplied incident coordinates. Device-reported location and accuracy are not independently verified."});
        await using var transaction=await db.Database.BeginTransactionAsync();
        var receipt=new CitizenReceipt{Id=item.Id,SubmissionKey=submissionKey};db.Grievances.Add(item);db.CitizenReceipts.Add(receipt);
        await new NotificationService(db).QueueAsync(item,null,"registered","Complaint received. Awaiting department assignment.");
        try
        {
            await db.SaveChangesAsync();
            if(photo is not null)
            {
                var attachment=await evidence.UploadAsync(item.Id,item.OwnerId,"Citizen portal",item.Version,photo,"Initial incident photo supplied by the citizen; not completion evidence.",photoConsent);
                item.IncidentPhotoId=attachment.Id;
                await db.SaveChangesAsync();
            }
            await transaction.CommitAsync();
        }
        catch(DbUpdateException)
        {
            await transaction.RollbackAsync();await transaction.DisposeAsync();db.ChangeTracker.Clear();
            var saved=await db.CitizenReceipts.AsNoTracking().SingleOrDefaultAsync(r=>r.SubmissionKey==submissionKey);
            if(saved is null)throw;
            return(await db.Grievances.AsNoTracking().SingleAsync(g=>g.Id==saved.Id),workflow.ReceiptCode(saved));
        }
        catch
        {
            await transaction.RollbackAsync();db.ChangeTracker.Clear();throw;
        }
        return(item,workflow.ReceiptCode(receipt));
    }

    private static void ValidateIncidentMedia(IFormFile? photo,double? latitude,double? longitude,double? accuracy,bool photoConsent)
    {
        if(latitude.HasValue!=longitude.HasValue || (accuracy.HasValue&&!latitude.HasValue)
            || (latitude.HasValue&&(!double.IsFinite(latitude.Value)||latitude.Value is < -90 or >90))
            || (longitude.HasValue&&(!double.IsFinite(longitude.Value)||longitude.Value is < -180 or >180))
            || (accuracy.HasValue&&(!double.IsFinite(accuracy.Value)||accuracy.Value is <0 or >100000)))
            throw new InvalidOperationException("Capture a valid incident location, or clear it and continue without coordinates.");
        if(photo is null)return;
        if(!photoConsent)throw new InvalidOperationException("Confirm permission to retain the incident photo, or remove it before submitting.");
        if(photo.Length is <=0 or >8*1024*1024)throw new InvalidOperationException("Choose a non-empty PNG or JPEG incident photo of at most 8 MB.");
        if(Path.GetExtension(photo.FileName).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidOperationException("Choose a PNG or JPEG incident photo.");
    }
}
