using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;
namespace Praja.Tests;
public class InboxTests
{
    private const string Text="No water supply in Ward 12, Mysuru, for three days. Please restore supply.";
    [Fact]
    public async Task SourceIdsAreScopedToPlatformHost()
    {
        await using var env=new Fixture();await env.Init();
        var first=await env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Social media","English","https://one.example.test/post/1","post-1",true);
        var second=await env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Social media","English","https://two.example.test/post/1","post-1",true);
        Assert.NotEqual(first.Id,second.Id);Assert.Equal(2,await env.Db.InboxMessages.CountAsync());
    }
    [Theory]
    [InlineData("http://source.example.test/post")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@source.example.test/post")]
    public async Task UnsafeSourceLinksAreRejected(string url)
    {
        await using var env=new Fixture();await env.Init();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Social media","English",url,null,true));
        Assert.Empty(await env.Db.InboxMessages.ToListAsync());
    }
    [Fact]
    public async Task CaptureRequiresPermissionAndOriginalIsUnchangedOnDismissal()
    {
        await using var env=new Fixture();await env.Init();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Text","English",null,null,false));
        var item=await env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Text","English",null,null,true);
        await env.Inbox.DismissAsync(item.Id,env.Owner.Id,1,"Already handled in an existing local case.","Reviewer");
        Assert.Equal(Text,item.OriginalText);Assert.Equal("Dismissed",item.State);Assert.Equal(2,item.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>env.Inbox.DismissAsync(item.Id,env.Owner.Id,1,"Another review reason for this message.","Reviewer"));
    }
    [Fact]
    public async Task DuplicateMatchesAndFindAreOwnerScoped()
    {
        await using var env=new Fixture();await env.Init();
        var other=new Operator{Email="other@example.test",Name="Other",PasswordHash="unused",BootstrapSlot=2};env.Db.Operators.Add(other);await env.Db.SaveChangesAsync();
        var source=await env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Text","English",null,null,true);
        await env.Inbox.CaptureAsync(other.Id,"Other",Text,"Text","English",null,null,true);
        Assert.Null(await env.Inbox.FindAsync(source.Id,other.Id));Assert.Empty(await env.Inbox.DuplicatesAsync(source));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>env.Cases.CreateFromInboxAsync(source.Id,1,other.Id,"Other",Text,"English","Mysuru","Ward 12",true));
        Assert.Empty(await env.Db.Grievances.ToListAsync());
    }
    [Fact]
    public async Task CaseInsertRollsBackIfInboxUpdateFails()
    {
        await using var env=new Fixture();await env.Init();
        var item=await env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Text","English",null,null,true);
        await env.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailInboxUpdate BEFORE UPDATE ON InboxMessages BEGIN SELECT RAISE(ABORT,'test update failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(()=>env.Cases.CreateFromInboxAsync(item.Id,1,env.Owner.Id,"Reviewer",Text,"English","Mysuru","Ward 12",true));
        Assert.Empty(await env.Db.Grievances.ToListAsync());Assert.Equal("Pending",(await env.Db.InboxMessages.SingleAsync()).State);
    }
    [Fact]
    public async Task ConcurrentReviewPreventsAnExtraDraft()
    {
        await using var env=new Fixture();await env.Init();
        var item=await env.Inbox.CaptureAsync(env.Owner.Id,"Reviewer",Text,"Text","English",null,null,true);
        await using(var competing=new WorkspaceDb(env.Options)){
            var changed=await competing.InboxMessages.SingleAsync();changed.State="Dismissed";changed.Version++;await competing.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>env.Cases.CreateFromInboxAsync(item.Id,1,env.Owner.Id,"Reviewer",Text,"English","Mysuru","Ward 12",true));
        Assert.Empty(await env.Db.Grievances.ToListAsync());Assert.Equal("Dismissed",(await env.Db.InboxMessages.SingleAsync()).State);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly SqliteConnection connection=new("Data Source=:memory:");
        public DbContextOptions<WorkspaceDb> Options{get;}
        public WorkspaceDb Db{get;}
        public InboxService Inbox{get;}
        public CaseService Cases{get;}
        public Operator Owner{get;}=new(){Email="owner@example.test",Name="Owner",PasswordHash="unused"};
        public Fixture(){Options=new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options;Db=new(Options);Inbox=new(Db);Cases=new(Db,new DraftingService(new ConfigurationBuilder().Build(),new NoProvider(),NullLogger<DraftingService>.Instance));}
        public async Task Init(){await connection.OpenAsync();await Db.Database.EnsureCreatedAsync();Db.Operators.Add(Owner);await Db.SaveChangesAsync();}
        public async ValueTask DisposeAsync(){await Db.DisposeAsync();await connection.DisposeAsync();}
    }
    private sealed class NoProvider:IHttpClientFactory{public HttpClient CreateClient(string name)=>throw new InvalidOperationException("No provider expected.");}
}
