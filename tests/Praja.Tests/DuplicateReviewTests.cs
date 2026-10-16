using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.RegularExpressions;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class DuplicateReviewTests
{
    private const string Text = "No water supply in Ward 12, Mysuru, for three days. Please restore supply.";
    private const string Reordered = "Please restore supply. For three days, Mysuru Ward 12 has no water supply.";
    private const string Reason = "Reviewed the source and confirmed this reports the same incident.";

    [Fact]
    public async Task DuplicateReviewMigrationPreservesExistingInboxAndCaseData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var owner = new Operator { Email="duplicate-migration@example.test",Name="Existing officer",PasswordHash="preserved-hash" };
        var target = new Grievance { OwnerId=owner.Id,Reference="DUPLICATE-MIGRATION",OriginalText=Text,DraftText="Preserve this reviewed draft exactly.",Version=7,Status="Registered",Department="water" };
        var source = new InboxMessage { OwnerId=owner.Id,OriginalText=Text,Channel="Email",Language="English",SourceUrl="https://mail.google.com/mail/u/0/#inbox/test-message",ExternalId="gmail:original-message",Fingerprint=InboxService.Fingerprint(Text),CapturedBy="Original officer",State="Converted",GrievanceId=target.Id,Version=4,ReviewNote="Existing source review must stay unchanged.",ReviewedBy="Existing reviewer",ReviewedUtc=new DateTime(2026,10,1,12,0,0,DateTimeKind.Utc) };
        db.Operators.Add(owner); db.Grievances.Add(target); db.InboxMessages.Add(source); await db.SaveChangesAsync();
        var migrations = db.Database.GetMigrations().ToArray();
        var added = Array.FindIndex(migrations,m=>m.EndsWith("_InboxDuplicateReview",StringComparison.Ordinal));
        Assert.True(added>0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[added-1]);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var saved = await db.InboxMessages.SingleAsync();
        Assert.Equal(source.Id,saved.Id); Assert.Equal(Text,saved.OriginalText); Assert.Equal(source.SourceUrl,saved.SourceUrl); Assert.Equal(source.ExternalId,saved.ExternalId);
        Assert.Equal("Email",saved.Channel); Assert.Equal("Converted",saved.State); Assert.Equal(target.Id,saved.GrievanceId); Assert.Equal(4,saved.Version);
        Assert.Equal(source.ReviewNote,saved.ReviewNote); Assert.Equal(source.ReviewedBy,saved.ReviewedBy); Assert.Equal(source.ReviewedUtc,saved.ReviewedUtc); Assert.Null(saved.DuplicateOfId);
        var savedCase = await db.Grievances.SingleAsync(); Assert.Equal(Text,savedCase.OriginalText); Assert.Equal(target.DraftText,savedCase.DraftText); Assert.Equal(7,savedCase.Version);
        Assert.Equal("preserved-hash",(await db.Operators.SingleAsync()).PasswordHash);
    }

    [Fact]
    public async Task RenderedReviewRequiresConfirmationThenLinksAndRejectsStaleRepeat()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new(){AllowAutoRedirect=false});
        await Setup(client);
        Guid sourceId, caseId;
        using(var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner = await db.Operators.SingleAsync();
            var source = await scope.ServiceProvider.GetRequiredService<InboxService>().CaptureAsync(owner.Id,"Reviewer",Text,"Email","English",null,null,true);
            var target = new Grievance {OwnerId=owner.Id,Reference="DUPLICATE-HTTP",OriginalText=Text,Title="Water supply complaint",Status="Registered",Department="water"};
            db.Grievances.Add(target); await db.SaveChangesAsync(); sourceId=source.Id; caseId=target.Id;
        }
        var path = "/Intake/Details/"+sourceId;
        var page = await client.GetStringAsync(path);
        Assert.Contains("DUPLICATE-HTTP",page); Assert.Contains("LinkDuplicate",page);
        async Task<HttpResponseMessage> Submit(bool confirmed) => await client.PostAsync(path+"?handler=LinkDuplicate",new FormUrlEncodedContent(new Dictionary<string,string>{
            ["__RequestVerificationToken"]=await Token(client,path),["Version"]="1",["CaseId"]=caseId.ToString(),["ReviewNote"]=Reason,["ConfirmDuplicate"]=confirmed.ToString()
        }));
        Assert.Equal(HttpStatusCode.OK,(await Submit(false)).StatusCode);
        using(var scope = app.Services.CreateScope()) Assert.Equal("Pending",(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.SingleAsync()).State);
        Assert.Equal(HttpStatusCode.Redirect,(await Submit(true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Submit(true)).StatusCode);
        using(var scope = app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var saved=await db.InboxMessages.SingleAsync();
            Assert.Equal("Duplicate",saved.State); Assert.Equal(caseId,saved.DuplicateOfId); Assert.Single(await db.Grievances.ToListAsync());
            Assert.Single(await db.AuditEntries.ToListAsync());
        }
    }

    [Fact]
    public async Task AnonymousReviewCannotReadOrMutateDuplicateSources()
    {
        using var app=new TestApp(); using var officer=app.CreateClient(new(){AllowAutoRedirect=false}); await Setup(officer);
        Guid sourceId;
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); var owner=await db.Operators.SingleAsync();
            sourceId=(await scope.ServiceProvider.GetRequiredService<InboxService>().CaptureAsync(owner.Id,"Reviewer",Text,"Email","English",null,null,true)).Id;
        }
        using var anonymous=app.CreateClient(new(){AllowAutoRedirect=false}); var path="/Intake/Details/"+sourceId;
        Assert.Equal(HttpStatusCode.Redirect,(await anonymous.GetAsync(path)).StatusCode);
        var response=await anonymous.PostAsync(path+"?handler=KeepSeparate",new FormUrlEncodedContent(new Dictionary<string,string>{["Version"]="1",["ReviewNote"]=Reason}));
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest);
        using var check=app.Services.CreateScope(); Assert.Equal(1,(await check.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.SingleAsync()).Version);
    }

    private static async Task Setup(HttpClient client)
    {
        var response=await client.PostAsync("/Setup",new FormUrlEncodedContent(new Dictionary<string,string>{["__RequestVerificationToken"]=await Token(client,"/Setup"),["Input.Name"]="Duplicate reviewer",["Input.Email"]="duplicate-http@example.test",["Input.Password"]="Duplicate-Test-Password-72!"}));
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);
    }
    private static async Task<string> Token(HttpClient client,string path)
    {
        var html=await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    [Theory]
    [InlineData("  NO WATER SUPPLY IN WARD 12, MYSURU, FOR THREE DAYS. PLEASE RESTORE SUPPLY. ")]
    [InlineData(Reordered)]
    [InlineData("No water supply in Ward 12, Mysuru, for three days. Kindly restore supply.")]
    public void MatchingAllowsCaseWhitespaceAndConservativeRewording(string other)
    {
        Assert.False(string.IsNullOrWhiteSpace(InboxService.MatchReason(Text, other)));
        Assert.False(string.IsNullOrWhiteSpace(InboxService.MatchReason(other, Text)));
    }

    [Theory]
    [InlineData("No water supply in Ward 13, Mysuru, for three days. Please restore supply.")]
    [InlineData("No water supply in Ward 12, Mysuru, for 7 days. Please restore supply.")]
    [InlineData("The street lights have failed near the railway station and need repair.")]
    [InlineData("Please restore supply.")]
    public void ConflictingNumbersUnrelatedAndWeakOverlapDoNotMatch(string other)
    {
        Assert.Null(InboxService.MatchReason(Text, other));
    }

    [Fact]
    public async Task CandidatesAreOwnerScopedAndExplainTheirMatch()
    {
        await using var f = new Fixture(); await f.Init();
        var item = await f.Capture(Text);
        var same = await f.Capture(Reordered);
        var registered = await f.Case(Text);
        var foreign = await f.OtherOwner();
        await f.Inbox.CaptureAsync(foreign.Id,"Other",Text,"Email","English",null,null,true);
        var foreignCase = await f.Case(Text,foreign.Id);
        var result = await f.Inbox.DuplicatesAsync(item);
        Assert.Contains(result, x => x.Id == same.Id && !x.IsCase && !string.IsNullOrWhiteSpace(x.Reason));
        Assert.Contains(result, x => x.Id == registered.Id && x.IsCase && !string.IsNullOrWhiteSpace(x.Reason));
        Assert.DoesNotContain(result,x => x.Id == foreignCase.Id || x.Id == item.Id);
        Assert.Equal(2,result.Count);
    }

    [Fact]
    public async Task SeveralChannelsCanLinkOneCaseWithoutCreatingMoreCasesOrChangingOriginals()
    {
        await using var f = new Fixture(); await f.Init();
        var target = await f.Case(Text);
        var email = await f.Capture(Text,"Email");
        var social = await f.Capture(Reordered,"Social media");
        await f.Inbox.ReviewDuplicateAsync(email.Id,f.Owner.Id,1,target.Id,Reason,"Reviewer");
        await f.Inbox.ReviewDuplicateAsync(social.Id,f.Owner.Id,1,target.Id,Reason,"Reviewer");
        f.Db.ChangeTracker.Clear();
        var records = await f.Db.InboxMessages.ToListAsync();
        Assert.All(records, item => { Assert.Equal("Duplicate",item.State); Assert.Equal(target.Id,item.DuplicateOfId); Assert.Null(item.GrievanceId); Assert.Equal(2,item.Version); Assert.Equal("Reviewer",item.ReviewedBy); Assert.NotNull(item.ReviewedUtc); });
        Assert.Contains(records,x => x.OriginalText == Text && x.Channel == "Email");
        Assert.Contains(records,x => x.OriginalText == Reordered && x.Channel == "Social media");
        Assert.Single(await f.Db.Grievances.ToListAsync());
        var history = await f.Db.AuditEntries.Where(x => x.GrievanceId == target.Id).ToListAsync();
        Assert.Contains(history,x => x.Actor == "Reviewer" && x.Action.Contains(email.Id.ToString()));
        Assert.Contains(history,x => x.Actor == "Reviewer" && x.Action.Contains(social.Id.ToString()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Cases.CreateFromInboxAsync(email.Id,2,f.Owner.Id,"Reviewer",Text,"English","Mysuru","Ward 12",true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inbox.ReviewDuplicateAsync(email.Id,f.Owner.Id,2,target.Id,Reason,"Reviewer"));
        Assert.Single(await f.Db.Grievances.ToListAsync());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("unmatched")]
    [InlineData("draft")]
    [InlineData("sample")]
    [InlineData("missing")]
    public async Task IneligibleTargetsCannotConsumeTheSource(string kind)
    {
        await using var f = new Fixture(); await f.Init();
        var item = await f.Capture(Text);
        var owner = kind == "foreign" ? (await f.OtherOwner()).Id : f.Owner.Id;
        var target = await f.Case(kind == "unmatched" ? "Street lights failed near railway station. Please repair them urgently." : Text,owner);
        if(kind == "draft") target.Status = "Draft";
        if(kind == "sample") target.IsSample = true;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inbox.ReviewDuplicateAsync(item.Id,f.Owner.Id,1,kind == "missing" ? Guid.NewGuid() : target.Id,Reason,"Reviewer"));
        f.Db.ChangeTracker.Clear();
        var unchanged = await f.Db.InboxMessages.SingleAsync();
        Assert.Equal("Pending",unchanged.State); Assert.Equal(1,unchanged.Version); Assert.Null(unchanged.DuplicateOfId);
        Assert.Empty(await f.Db.AuditEntries.ToListAsync());
    }

    [Theory]
    [InlineData(0,"Reviewed and confirmed this is the same complaint.")]
    [InlineData(1,"too short")]
    [InlineData(1,"")]
    public async Task VersionAndExplanationAreRequired(int version,string reason)
    {
        await using var f = new Fixture(); await f.Init();
        var item = await f.Capture(Text); var target = await f.Case(Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inbox.ReviewDuplicateAsync(item.Id,f.Owner.Id,version,target.Id,reason,"Reviewer"));
        Assert.Equal("Pending",item.State); Assert.Null(item.DuplicateOfId);
    }

    [Fact]
    public async Task OversizedReviewAndForeignSourceAreRejected()
    {
        await using var f = new Fixture(); await f.Init();
        var item = await f.Capture(Text); var target = await f.Case(Text); var other = await f.OtherOwner();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inbox.ReviewDuplicateAsync(item.Id,f.Owner.Id,1,target.Id,new string('x',501),"Reviewer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inbox.ReviewDuplicateAsync(item.Id,other.Id,1,target.Id,Reason,"Reviewer"));
        Assert.Equal("Pending",item.State); Assert.Equal(1,item.Version);
    }

    [Fact]
    public async Task KeepSeparatePreservesSourceAndAllowsNormalDraftAndRegistration()
    {
        await using var f = new Fixture(); await f.Init();
        var item = await f.Capture(Text); await f.Case(Text);
        await f.Inbox.ReviewDuplicateAsync(item.Id,f.Owner.Id,1,null,"Reviewed source: a separate occurrence needs its own case.","Reviewer");
        Assert.Equal("Pending",item.State); Assert.Equal(2,item.Version); Assert.Null(item.DuplicateOfId);
        Assert.Equal(Text,item.OriginalText); Assert.Contains("separate",item.ReviewNote,StringComparison.OrdinalIgnoreCase);
        var draft = await f.Cases.CreateFromInboxAsync(item.Id,2,f.Owner.Id,"Reviewer",Text,"English","Mysuru","Ward 12",true);
        await f.Cases.RegisterAsync(draft,draft.Version,draft.Title,draft.DraftText,"water",true,"Reviewer");
        Assert.Equal("Registered",draft.Status); Assert.Equal("Converted",item.State); Assert.Equal(draft.Id,item.GrievanceId);
        Assert.Equal(2,await f.Db.Grievances.CountAsync());
    }

    [Fact]
    public async Task ConcurrentSourceChangeRollsBackDuplicateLinkAndTargetAudit()
    {
        await using var f = new Fixture(); await f.Init();
        var item = await f.Capture(Text); var target = await f.Case(Text);
        await using(var competing = new WorkspaceDb(f.Options))
        {
            var changed = await competing.InboxMessages.SingleAsync(); changed.State = "Dismissed"; changed.Version++;
            await competing.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inbox.ReviewDuplicateAsync(item.Id,f.Owner.Id,1,target.Id,Reason,"Reviewer"));
        f.Db.ChangeTracker.Clear();
        Assert.Equal("Dismissed",(await f.Db.InboxMessages.SingleAsync()).State);
        Assert.Empty(await f.Db.AuditEntries.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public DbContextOptions<WorkspaceDb> Options { get; }
        public WorkspaceDb Db { get; }
        public InboxService Inbox { get; }
        public CaseService Cases { get; }
        public Operator Owner { get; } = new(){Email="duplicate-owner@example.test",Name="Owner",PasswordHash="unused"};
        public Fixture()
        {
            Options = new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options;
            Db = new(Options); Inbox = new(Db);
            Cases = new(Db,new DraftingService(new ConfigurationBuilder().Build(),new NoProvider(),NullLogger<DraftingService>.Instance));
        }
        public async Task Init() { await connection.OpenAsync(); await Db.Database.EnsureCreatedAsync(); Db.Operators.Add(Owner); await Db.SaveChangesAsync(); }
        public Task<InboxMessage> Capture(string text,string channel="Text") => Inbox.CaptureAsync(Owner.Id,"Reviewer",text,channel,"English",null,null,true);
        public async Task<Operator> OtherOwner()
        {
            var item = new Operator { Email="duplicate-other@example.test",Name="Other",PasswordHash="unused",BootstrapSlot=2 };
            Db.Operators.Add(item); await Db.SaveChangesAsync(); return item;
        }
        public async Task<Grievance> Case(string text,Guid? owner=null)
        {
            var item = new Grievance { OwnerId=owner??Owner.Id,Reference="TEST-"+Guid.NewGuid().ToString("N")[..12],OriginalText=text,Title="Water supply complaint",Department="water",Status="Registered" };
            Db.Grievances.Add(item); await Db.SaveChangesAsync(); return item;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class NoProvider : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No provider expected for local duplicate review.");
    }
}
