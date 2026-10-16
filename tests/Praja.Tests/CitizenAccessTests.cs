using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class CitizenAccessTests
{
    [Fact]
    public async Task CodeReturnsOnlyPublicStatusAndChangesAsCaseProgresses()
    {
        using var fixture = await Fixture.CreateAsync();
        var code = await fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId);
        Assert.NotNull(code);
        Assert.Equal(fixture.Clock.GetUtcNow().AddDays(7), code.ExpiresUtc);
        var status = await fixture.Service.GetStatusAsync(code.Code);
        Assert.NotNull(status);
        Assert.Equal("PRJ-TEST-001", status.Reference);
        Assert.Equal("Water supply", status.Service);
        Assert.Equal("Registered", status.Status);
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain("OwnerId", json);
        Assert.DoesNotContain("Id", json);
        fixture.Case.Status = "InProgress";
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("InProgress", (await fixture.Service.GetStatusAsync(code.Code))!.Status);
        Assert.NotEqual(code.Code, (await fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId))!.Code);
    }

    [Fact]
    public async Task ExpiredCodeFailsAtExpiryAndLifetimeCannotExceedSevenDays()
    {
        using var fixture = await Fixture.CreateAsync();
        var code = (await fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId, TimeSpan.FromHours(24)))!;
        fixture.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.NotNull(await fixture.Service.GetStatusAsync(code.Code));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await fixture.Service.GetStatusAsync(code.Code));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId, TimeSpan.FromDays(8)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId, TimeSpan.Zero));
    }

    [Fact]
    public async Task TamperedMalformedAndDifferentProtectorCodesFailGenerically()
    {
        using var fixture = await Fixture.CreateAsync();
        var code = (await fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId))!.Code;
        var changed = code.ToCharArray();
        changed[code.Length / 2] = changed[code.Length / 2] == 'A' ? 'B' : 'A';
        Assert.Null(await fixture.Service.GetStatusAsync(new string(changed)));
        Assert.Null(await fixture.Service.GetStatusAsync("invalid"));
        Assert.Null(await fixture.Service.GetStatusAsync(null));
        Assert.Null(await fixture.Service.GetStatusAsync(new string('A', 3000)));
        var other = new CitizenAccessService(fixture.Db, new EphemeralDataProtectionProvider(), fixture.Clock);
        Assert.Null(await other.GetStatusAsync(code));
    }

    [Fact]
    public async Task IssueRequiresOwnerAndRegisteredCaseAndLookupRechecksOwnershipAndDraftState()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Null(await fixture.Service.IssueAsync(fixture.Case.Id, Guid.NewGuid()));
        Assert.Null(await fixture.Service.IssueAsync(Guid.NewGuid(), fixture.Case.OwnerId));
        var code = (await fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId))!.Code;
        fixture.Case.Status = "Draft";
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await fixture.Service.IssueAsync(fixture.Case.Id, fixture.Case.OwnerId));
        Assert.Null(await fixture.Service.GetStatusAsync(code));
        fixture.Case.Status = "Registered";
        var second = new Operator { BootstrapSlot = 2, Email = "second@example.test" };
        fixture.Db.Operators.Add(second);
        fixture.Case.OwnerId = second.Id;
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await fixture.Service.GetStatusAsync(code));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }

    private sealed class Fixture(SqliteConnection connection, WorkspaceDb db, Grievance item, TestClock clock) : IDisposable
    {
        public WorkspaceDb Db { get; } = db;
        public Grievance Case { get; } = item;
        public TestClock Clock { get; } = clock;
        public CitizenAccessService Service { get; } = new(db, new EphemeralDataProtectionProvider(), clock);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var owner = new Operator { Email = "test@example.test" };
            var item = new Grievance
            {
                OwnerId = owner.Id, Reference = "PRJ-TEST-001", Title = "Water supply in Ward 12", Status = "Registered", Department = "water", District = "Bengaluru Urban",
                OriginalText = "PRIVATE original", DraftText = "PRIVATE draft", EvidenceReference = "PRIVATE evidence", ResolutionReport = "PRIVATE resolution", Summary = "PRIVATE summary", Location = "PRIVATE address"
            };
            db.Operators.Add(owner);
            db.Grievances.Add(item);
            await db.SaveChangesAsync();
            return new(connection, db, item, new());
        }
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }
}
