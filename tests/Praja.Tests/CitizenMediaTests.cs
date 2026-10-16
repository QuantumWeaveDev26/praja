using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
using static Praja.Tests.TeamWorkflowTests;

namespace Praja.Tests;

public class CitizenMediaTests
{
    private const string Png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP2kAAAAASUVORK5CYII=";
    private static async Task<Guid> Contractor(IServiceProvider services,Guid admin)
    {
        await services.GetRequiredService<WorkflowService>().CreateOfficerAsync(admin,"Field crew","crew@example.test","Synthetic-password-123","Contractor","water");
        return (await services.GetRequiredService<WorkspaceDb>().Operators.SingleAsync(o=>o.Email=="crew@example.test")).Id;
    }
    private static async Task<HttpResponseMessage> SubmitPhoto(HttpClient client,Dictionary<string,string> fields,byte[] bytes,string name="incident.png")
    {
        var page=await client.GetStringAsync("/Submit");
        var token=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(page,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        using var body=new MultipartFormDataContent();body.Add(new StringContent(token),"__RequestVerificationToken");
        foreach(var field in fields.Where(f=>f.Key!="__RequestVerificationToken"))body.Add(new StringContent(field.Value),field.Key);
        body.Add(new ByteArrayContent(bytes),"Photo",name);return await client.PostAsync("/Submit",body);
    }
    [Fact]
    public async Task PhotoAndGpsSubmissionThroughContractorAndBothReviewsEndsInCitizenApproval()
    {
        using var app=new TestApp();Guid officerId,crewId;
        using(var scope=app.Services.CreateScope()){
            var ids=await Seed(scope.ServiceProvider);officerId=ids.Officer;crewId=await Contractor(scope.ServiceProvider,ids.Admin);
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var admin=await db.Operators.SingleAsync(o=>o.Id==ids.Admin);admin.PasswordHash=WorkspaceIdentity.Hasher.HashPassword(admin,"Synthetic-password-123");var crewAccount=await db.Operators.SingleAsync(o=>o.Id==crewId);crewAccount.MustChangePassword=false;await db.SaveChangesAsync();
        }
        using var citizen=app.CreateClient(new(){AllowAutoRedirect=false});
        var key=Guid.NewGuid().ToString();var fields=new Dictionary<string,string>{["SubmissionKey"]=key,["Original"]="Fictional test: no water supply in Ward 12, Mysuru, for three days.",["District"]="Mysuru",["Location"]="Ward 12",["Language"]="English",["Consent"]="true",["Sample"]="true"};
        var incidentBytes=Convert.FromBase64String(Png);
        fields["PhotoConsent"]="true";fields["Latitude"]="12.3051";fields["Longitude"]="76.6551";fields["Accuracy"]="18";
        var submitted=await SubmitPhoto(citizen,fields,incidentBytes);var receiptPage=await submitted.Content.ReadAsStringAsync();Assert.Contains("Complaint received",receiptPage);
        var code=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(receiptPage,"<textarea[^>]*id=\"receipt-code\"[^>]*>(.*?)</textarea>",System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value.Trim());Assert.NotEmpty(code);
        await SubmitPhoto(citizen,fields,incidentBytes);
        Guid id,incidentPhoto;int initialVersion;using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var receipt=await db.CitizenReceipts.SingleAsync();id=receipt.Id;Assert.Equal(2,await db.Grievances.CountAsync());var item=await db.Grievances.SingleAsync(g=>g.Id==id);initialVersion=item.Version;incidentPhoto=item.IncidentPhotoId!.Value;Assert.Equal(12.3051,item.IncidentLatitude);Assert.Equal(76.6551,item.IncidentLongitude);Assert.Equal(18d,item.IncidentAccuracyMetres);var saved=await db.EvidenceAttachments.SingleAsync();Assert.False(saved.ProtectedContent.SequenceEqual(incidentBytes));}
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Open",new(){["Code"]=code})).StatusCode);
        Assert.Contains("awaiting officer assignment",await citizen.GetStringAsync("/Track"));
        Assert.Contains("Submitted incident evidence",await citizen.GetStringAsync("/Track"));
        Assert.Equal(incidentBytes,await (await Post(citizen,"/Track?handler=IncidentPhoto",new())).Content.ReadAsByteArrayAsync());
        using var stranger=app.CreateClient(new(){AllowAutoRedirect=false});Assert.Equal(System.Net.HttpStatusCode.NotFound,(await Post(stranger,"/Track?handler=IncidentPhoto",new())).StatusCode);
        using var adminClient=app.CreateClient(new(){AllowAutoRedirect=false});using var department=app.CreateClient(new(){AllowAutoRedirect=false});using var reviewer=app.CreateClient(new(){AllowAutoRedirect=false});using var crew=app.CreateClient(new(){AllowAutoRedirect=false});
        foreach(var (client,email) in new[]{(adminClient,"admin@example.test"),(department,"water@example.test"),(reviewer,"review@example.test"),(crew,"crew@example.test")})Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(client,"/Account/Login",new(){["Email"]=email,["Password"]="Synthetic-password-123"})).StatusCode);
        var path="/Work/Case/"+id;
        Assert.Equal(incidentBytes,await adminClient.GetByteArrayAsync(path+"?handler=Download&file="+incidentPhoto));
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await department.GetAsync(path+"?handler=Download&file="+incidentPhoto)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(adminClient,path+"?handler=Assign",new(){["Version"]=initialVersion.ToString(),["Officer"]=officerId.ToString(),["Due"]=DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd")})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=Act",new(){["Version"]="2",["Action"]="Accept",["Note"]="Accepted the water complaint"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=AssignContractor",new(){["Version"]="3",["Officer"]=crewId.ToString(),["Note"]="Assign water repair field team"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,(await crew.GetAsync("/Work/Team")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await crew.GetAsync("/AiSettings")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(crew,path+"?handler=Act",new(){["Version"]="4",["Action"]="FieldAccept",["Note"]="Field team accepts repair assignment"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(crew,path+"?handler=Act",new(){["Version"]="5",["Action"]="FieldProgress",["Note"]="Repair crew reached the location"})).StatusCode);
        using(var validation=app.Services.CreateScope()){
            await Assert.ThrowsAsync<InvalidOperationException>(()=>validation.ServiceProvider.GetRequiredService<WorkflowService>().ActAsync(crewId,id,6,"FieldComplete","Original incident photo cannot prove a repair",incidentPhoto,12.3,76.6,DateTime.UtcNow,true));
        }
        var uploadPage=await crew.GetStringAsync(path);var token=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(uploadPage,"name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        var bytes=incidentBytes.Concat(new byte[]{0}).ToArray();
        int uploadVersion;using(var uploadScope=app.Services.CreateScope()){uploadVersion=(await uploadScope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync(g=>g.Id==id)).Version;}
        using var multipart=new MultipartFormDataContent();multipart.Add(new StringContent(token),"__RequestVerificationToken");multipart.Add(new StringContent(uploadVersion.ToString()),"Version");multipart.Add(new StringContent("true"),"Checked");multipart.Add(new StringContent("Fictional completion image"),"Note");multipart.Add(new ByteArrayContent(bytes),"Upload","completion.png");
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await crew.PostAsync(path+"?handler=Upload",multipart)).StatusCode);
        Guid photo;using(var scope=app.Services.CreateScope()){photo=(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().EvidenceAttachments.SingleAsync(e=>e.Id!=incidentPhoto)).Id;}
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(crew,path+"?handler=Act",new(){["Version"]="6",["Action"]="FieldComplete",["Note"]="Fictional test: water supply restored and tested onsite.",["Photo"]=photo.ToString(),["Latitude"]="12.3",["Longitude"]="76.6",["Captured"]=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm"),["Checked"]="true"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,(await Post(citizen,"/Track?handler=Photo",new())).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(department,path+"?handler=Act",new(){["Version"]="7",["Action"]="ApproveField",["Note"]="Officer inspected the contractor evidence",["Checked"]="true"})).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(reviewer,path+"?handler=Act",new(){["Version"]="8",["Action"]="Verify",["Note"]="Fictional test: report, photo and location checked.",["Checked"]="true"})).StatusCode);
        var photoResponse=await Post(citizen,"/Track?handler=Photo",new());Assert.Equal(bytes,await photoResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("Repair crew reached the location",await citizen.GetStringAsync("/Track"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect,(await Post(citizen,"/Track?handler=Respond",new(){["version"]="9",["decision"]="Confirm",["note"]="Fictional test: water supply works now."})).StatusCode);
        var closedPage=await citizen.GetStringAsync("/Track");Assert.Contains("Closed",closedPage);Assert.Contains("Citizen confirmed resolution",closedPage);
        using var final=app.Services.CreateScope();var closed=await final.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync(g=>g.Id==id);Assert.Equal("Closed",closed.Status);Assert.Equal("Confirmed",closed.CitizenFeedback);
        var notificationDb=final.ServiceProvider.GetRequiredService<WorkspaceDb>();
        var reviewerId=(await notificationDb.Operators.SingleAsync(o=>o.Email=="review@example.test")).Id;
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient==reviewerId.ToString()&&n.EventKey=="workflow:8"));
        Assert.True(await notificationDb.CaseNotifications.AnyAsync(n=>n.CaseId==id&&n.Recipient=="citizen"&&n.Message.Contains("Citizen confirmed")));
        Assert.Contains("Case notifications",closedPage);
        var publicPage=await citizen.GetStringAsync("/Transparency");Assert.DoesNotContain("Ward 12",publicPage);Assert.DoesNotContain(code,publicPage);Assert.DoesNotContain("Fictional test",publicPage);
    }


    [Theory]
    [InlineData("bad-signature")]
    [InlineData("pdf")]
    [InlineData("no-consent")]
    [InlineData("oversize")]
    [InlineData("latitude")]
    [InlineData("longitude")]
    [InlineData("missing-longitude")]
    [InlineData("accuracy")]
    [InlineData("nan")]
    public async Task InvalidMediaCannotLeavePartialCaseReceiptNotificationOrEvidence(string invalid)
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();await Seed(scope.ServiceProvider);
        var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var service=scope.ServiceProvider.GetRequiredService<PublicComplaintService>();
        var beforeNotifications=await db.CaseNotifications.CountAsync();var bytes=Convert.FromBase64String(Png);var name="incident.png";
        if(invalid=="bad-signature")bytes="not an image"u8.ToArray();
        if(invalid=="pdf"){bytes="%PDF-1.7 test"u8.ToArray();name="incident.pdf";}
        using var stream=new MemoryStream(bytes);var length=invalid=="oversize"?8*1024*1024+1:bytes.Length;
        var file=new FormFile(stream,0,length,"Photo",name);
        double? lat=invalid=="latitude"?91:invalid=="nan"?double.NaN:12.3;
        double? lon=invalid=="longitude"?181:invalid=="missing-longitude"?null:76.6;
        var key=Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SubmitAsync(key,"Fictional no water supply in Ward 12 for three days.","Mysuru","Ward 12","English",true,true,photo:file,latitude:lat,longitude:lon,accuracy:invalid=="accuracy"?-1:12,photoConsent:invalid!="no-consent"));
        Assert.Single(await db.Grievances.ToListAsync());Assert.Empty(await db.CitizenReceipts.ToListAsync());Assert.Empty(await db.EvidenceAttachments.ToListAsync());Assert.Equal(beforeNotifications,await db.CaseNotifications.CountAsync());
        var retry=await service.SubmitAsync(key,"Fictional no water supply in Ward 12 for three days.","Mysuru","Ward 12","English",true,true);
        Assert.NotEmpty(retry.Code);Assert.Equal(2,await db.Grievances.CountAsync());Assert.Single(await db.CitizenReceipts.ToListAsync());
    }

    [Fact]
    public async Task TextAndOfflineCompatibleSubmissionNeedsNoPhotoOrLocationAndRetryCannotReplaceEvidence()
    {
        using var app=new TestApp();using var scope=app.Services.CreateScope();await Seed(scope.ServiceProvider);
        var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var service=scope.ServiceProvider.GetRequiredService<PublicComplaintService>();
        var key=Guid.NewGuid();var first=await service.SubmitAsync(key,"Fictional no water supply in Ward 12 for three days.","Mysuru","Ward 12","English",true,true);
        Assert.Null(first.Case.IncidentPhotoId);Assert.Null(first.Case.IncidentLatitude);Assert.Null(first.Case.IncidentLongitude);Assert.Null(first.Case.IncidentAccuracyMetres);
        var bytes=Convert.FromBase64String(Png);using var stream=new MemoryStream(bytes);var file=new FormFile(stream,0,bytes.Length,"Photo","incident.png");
        var retry=await service.SubmitAsync(key,"Fictional no water supply in Ward 12 for three days.","Mysuru","Ward 12","English",true,true,photo:file,latitude:12.3,longitude:76.6,accuracy:15,photoConsent:true);
        Assert.Equal(first.Case.Id,retry.Case.Id);Assert.NotEmpty(retry.Code);Assert.Null(retry.Case.IncidentPhotoId);Assert.Empty(await db.EvidenceAttachments.ToListAsync());Assert.Single(await db.CitizenReceipts.ToListAsync());
    }
}


