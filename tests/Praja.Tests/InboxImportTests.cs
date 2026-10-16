using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class InboxImportTests
{
    private const string Header = "channel,external_id,language,source_url,message\n";
    private const string Text = "No water supply in Ward 12 for three days. Please restore supply.";

    [Fact]
    public async Task DemoFixturesHaveReviewableLocalCategoriesAndIssues()
    {
        await using var env = new Fixture(); await env.Init();
        var preview = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", InboxImportService.SampleCsv);
        Assert.Equal(new[] { "water", "sanitation", "education" }, preview.Rows.Select(row => row.Route.Department));
        Assert.All(preview.Rows, row => { Assert.NotEmpty(row.Route.Category); Assert.NotEmpty(row.Route.Subcategory); });
        Assert.Empty(await env.Db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task QuotedCommaNewlineAndEscapedQuotesPreserveSourceText()
    {
        await using var env = new Fixture(); await env.Init();
        var original = "The citizen said \"no water\", in Ward 12.\nPlease restore supply.";
        var csv = "\uFEFF" + Header + "Email,e1,English,,\"" + original.Replace("\"", "\"\"") + "\"\n";
        var preview = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", csv);
        Assert.Equal(original, preview.Rows.Single().Message.OriginalText);
        var saved = await env.Imports.ImportAsync(env.Owner.Id, "Reviewer", preview.Token, true);
        Assert.Equal(original, saved.Single().Message.OriginalText);
    }

    [Fact]
    public async Task SameWordingWithDifferentIdsIsFlaggedButNotDiscarded()
    {
        await using var env = new Fixture(); await env.Init();
        var csv = Header + "Email,e1,English,," + Text + "\nField meeting,f1,English,," + Text;
        var preview = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", csv);
        Assert.All(preview.Rows, row => Assert.Equal(1, row.BatchMatches));
        var imported = await env.Imports.ImportAsync(env.Owner.Id, "Reviewer", preview.Token, true);
        Assert.Equal(2, imported.Count(r => r.Created));
        var later = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", Header + "Email,e2,English,," + Text);
        Assert.Equal(2, later.Rows.Single().Matches.Count);
    }

    [Fact]
    public async Task RepeatedIdInBatchCreatesOnlyOneInboxItem()
    {
        await using var env = new Fixture(); await env.Init();
        var row = "Email,e1,English,," + Text + "\n";
        var preview = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", Header + row + row);
        Assert.True(preview.Rows[1].RepeatedSource);
        var result = await env.Imports.ImportAsync(env.Owner.Id, "Reviewer", preview.Token, true);
        Assert.Single(result, r => r.Created); Assert.Single(result, r => !r.Created);
        Assert.Single(await env.Db.InboxMessages.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", Header + row + "Email,e1,English,,A changed message about another complaint in another place."));
    }

    [Fact]
    public async Task ImportFailureAfterFirstInsertRollsBackThatInsert()
    {
        await using var env = new Fixture(); await env.Init();
        var preview = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", InboxImportService.SampleCsv);
        await env.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailFieldImport BEFORE INSERT ON InboxMessages WHEN NEW.Channel = 'Field meeting' BEGIN SELECT RAISE(ABORT,'test import failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => env.Imports.ImportAsync(env.Owner.Id, "Reviewer", preview.Token, true));
        Assert.Empty(await env.Db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ExpiredReceiptCannotSaveAnyMessages()
    {
        await using var env = new Fixture(); await env.Init();
        var preview = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", InboxImportService.SampleCsv);
        env.Clock.Now = env.Clock.Now.AddMinutes(30);
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Imports.ImportAsync(env.Owner.Id, "Reviewer", preview.Token, true));
        Assert.Empty(await env.Db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task LargeUnicodeBatchFitsReceiptAndRoundTrips()
    {
        await using var env = new Fixture(); await env.Init();
        var original = string.Concat(Enumerable.Repeat("ನೀರು ", 1100));
        var csv = Header + string.Join("\n", Enumerable.Range(1, 5).Select(i => $"Email,e{i},Kannada,,{original}"));
        Assert.True(csv.Length < InboxImportService.MaximumCharacters);
        var preview = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", csv);
        Assert.True(preview.Token.Length < 200_000);
        var result = await env.Imports.ImportAsync(env.Owner.Id, "Reviewer", preview.Token, true);
        Assert.Equal(5, result.Count); Assert.All(result, r => Assert.Equal(original.Trim(), r.Message.OriginalText));
    }

    [Fact]
    public async Task RetryingDismissedItemPreservesItsReviewAndOriginalSource()
    {
        await using var env = new Fixture(); await env.Init();
        var csv = Header + "Email,e1,English,," + Text;
        var first = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", csv);
        var item = (await env.Imports.ImportAsync(env.Owner.Id, "Reviewer", first.Token, true)).Single().Message;
        await env.Inbox.DismissAsync(item.Id, env.Owner.Id, item.Version, "Already addressed in another local case.", "Reviewer");
        var later = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", csv);
        Assert.Equal(item.Id, later.Rows.Single().ExistingId);
        var retried = (await env.Imports.ImportAsync(env.Owner.Id, "New reviewer", later.Token, true)).Single();
        Assert.False(retried.Created); Assert.Equal("Dismissed", retried.Message.State);
        Assert.Equal("Reviewer", retried.Message.CapturedBy); Assert.Equal(item.ReviewNote, retried.Message.ReviewNote);
    }

    [Fact]
    public async Task OtherOwnersIdenticalSourceIdsRemainIndependent()
    {
        await using var env = new Fixture(); await env.Init();
        var other = new Operator { Email = "other@example.test", Name = "Other", BootstrapSlot = 2, PasswordHash = "unused" };
        env.Db.Operators.Add(other); await env.Db.SaveChangesAsync();
        var csv = Header + "Email,e1,English,," + Text;
        var first = await env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", csv);
        await env.Imports.ImportAsync(env.Owner.Id, "Reviewer", first.Token, true);
        var second = await env.Imports.PreviewAsync(other.Id, "Other", csv);
        Assert.Null(second.Rows.Single().ExistingId); Assert.Empty(second.Rows.Single().Matches);
        await env.Imports.ImportAsync(other.Id, "Other", second.Token, true);
        Assert.Equal(2, await env.Db.InboxMessages.CountAsync());
    }

    [Fact]
    public async Task OversizedAndEmptyBatchesAndUnsafeSourcesAreRejectedBeforeWrites()
    {
        await using var env = new Fixture(); await env.Init();
        var rows = Header + string.Join("\n", Enumerable.Range(1, 21).Select(i => $"Email,e{i},English,,{Text}"));
        foreach (var csv in new[] { Header, rows, new string('x', 32_769), Header + "Social media,p1,English,https://user:password@social.example.test/post," + Text })
            await Assert.ThrowsAsync<InvalidOperationException>(() => env.Imports.PreviewAsync(env.Owner.Id, "Reviewer", csv));
        Assert.Empty(await env.Db.InboxMessages.ToListAsync());
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public WorkspaceDb Db { get; }
        public InboxService Inbox { get; }
        public InboxImportService Imports { get; }
        public MutableClock Clock { get; } = new();
        public Operator Owner { get; } = new() { Email = "owner@example.test", Name = "Owner", PasswordHash = "unused" };
        public Fixture()
        {
            Db = new(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options);
            Inbox = new(Db); Imports = new(Db, Inbox, new EphemeralDataProtectionProvider(), Clock);
        }
        public async Task Init() { await connection.OpenAsync(); await Db.Database.EnsureCreatedAsync(); Db.Operators.Add(Owner); await Db.SaveChangesAsync(); }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
