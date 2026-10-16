using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task FailedClosureReloadsPersistedStateInsteadOfDisplayingUnsavedChanges()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options;
        using var first = new WorkspaceDb(options);
        await first.Database.EnsureCreatedAsync();
        var owner = new Operator { Name = "Test", Email = "test@example.test", PasswordHash = "unused" };
        first.Operators.Add(owner);
        var item = new Grievance
        {
            OwnerId = owner.Id, Reference = "TEST-01", OriginalText = "No drinking water in Ward 12", Status = "ClosureReview",
            ResolutionReport = "The damaged water valve was replaced and the water supply was restored in Ward 12 after an on-site inspection.",
            EvidenceReference = "WR-12", CitizenFeedback = "Confirmed"
        };
        first.Grievances.Add(item); await first.SaveChangesAsync();
        using var competing = new WorkspaceDb(options);
        var changed = await competing.Grievances.SingleAsync();
        changed.Version++; changed.CitizenFeedback = "Disagreed"; await competing.SaveChangesAsync();
        var config = new ConfigurationBuilder().Build();
        var drafting = new DraftingService(config, new UnusedFactory(), NullLogger<DraftingService>.Instance);
        var service = new CaseService(first, drafting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseAsync(item, 1, true, "Test officer"));
        var reloaded = await service.FindAsync(item.Id, owner.Id);
        Assert.Equal("ClosureReview", reloaded!.Status);
        Assert.Equal("Disagreed", reloaded.CitizenFeedback);
        Assert.Empty(reloaded.History);
    }
    private sealed class UnusedFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No provider call is expected.");
    }
}
