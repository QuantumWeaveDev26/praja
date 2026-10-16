using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class PerformanceAnalyticsTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PerformanceCountsOperationalCasesAndRecordedClosureTimes()
    {
        var unassigned = Row("Registered", -5);
        var active = Row("InProgress", -4);
        var awaiting = Row("AwaitingCitizen", -3);
        var closed = Row("Closed", -4); closed.ClosedUtc = Now.AddDays(-2);
        var closed2 = Row("Closed", -3); closed2.ClosedUtc = Now.AddDays(-2);
        var draft = Row("Draft", -10);
        var result = AnalyticsService.Performance(Snapshot(unassigned, active, awaiting, closed, closed2, draft),
            [Work(active, Now.AddDays(-1), Now.AddDays(-2)), Work(awaiting, Now.AddDays(1)), Work(closed, Now.AddDays(-3))], Now).Single();
        Assert.Equal("water", result.Department);
        Assert.Equal(5, result.Total);
        Assert.Equal(3, result.Open);
        Assert.Equal(2, result.Closed);
        Assert.Equal(1, result.Unassigned);
        Assert.Equal(1, result.Overdue);
        Assert.Equal(1, result.AwaitingCitizen);
        Assert.Equal(1, result.Reopened);
        Assert.Equal(2, result.TimedClosures);
        Assert.Equal(36d, result.AverageClosureHours);
        Assert.Equal(5d, result.OldestOpenDays);
    }

    [Fact]
    public void DueAndReopenBoundariesRejectInvalidAndFutureTimestamps()
    {
        var rows = Enumerable.Range(0, 5).Select(_ => Row("InProgress", -2)).ToArray();
        var work = new[]
        {
            Work(rows[0], Now, Now.AddDays(-2)), // Due now: not overdue; reopen at creation valid.
            Work(rows[1], Now.AddTicks(-1), Now),
            Work(rows[2], Now.AddDays(-3), Now.AddDays(-3)),
            Work(rows[3], Now.AddDays(1), Now.AddTicks(1)),
            Work(rows[4], Now.AddDays(-2)) // Due at creation: valid.
        };
        var result = AnalyticsService.Performance(Snapshot(rows), work, Now).Single();
        Assert.Equal(2, result.Overdue);
        Assert.Equal(2, result.Reopened);
        Assert.Equal(0, result.Unassigned);
    }

    [Fact]
    public void OnlyValidClosedCasesContributeToAverageAndEmptyMetricsStayNull()
    {
        var missing = Row("Closed", -2);
        var negative = Row("Closed", -2); negative.ClosedUtc = Now.AddDays(-3);
        var future = Row("Closed", -2); future.ClosedUtc = Now.AddTicks(1);
        var active = Row("InProgress", -2); active.ClosedUtc = Now.AddDays(-1);
        var result = AnalyticsService.Performance(Snapshot(missing, negative, future, active), [], Now).Single();
        Assert.Equal(3, result.Closed);
        Assert.Equal(0, result.TimedClosures);
        Assert.Null(result.AverageClosureHours);
        var allClosed = AnalyticsService.Performance(Snapshot(missing, negative, future), [], Now).Single();
        Assert.Null(allClosed.OldestOpenDays);
        Assert.Empty(AnalyticsService.Performance(Snapshot(Row("Draft", -1)), [], Now));
        Assert.Empty(AnalyticsService.Recurrences(Snapshot(), [], Now));
    }

    [Fact]
    public void WorkflowsAreJoinedByCaseIdAndDoNotInflateCounts()
    {
        var registered = Row("Registered", -2);
        var active = Row("InProgress", -2);
        var unrelated = Row("Registered", -2);
        var assigned = Work(registered, Now.AddDays(1));
        var result = AnalyticsService.Performance(Snapshot(registered, active),
            [assigned, assigned, Work(unrelated, Now.AddDays(-1), Now.AddDays(-1))], Now).Single();
        Assert.Equal(2, result.Total);
        Assert.Equal(0, result.Unassigned);
        Assert.Equal(0, result.Overdue);
        Assert.Equal(0, result.Reopened);
    }

    [Fact]
    public void RecurrencesNormalizePlaceWhitespaceAndCaseButKeepDifferentIssuesSeparate()
    {
        var a = Row("Registered", -4); a.District = " Mysuru "; a.Location = "Ward   12";
        var b = Row("InProgress", -3); b.District = "mysuru"; b.Location = " ward\t12 ";
        var otherPlace = Row("Registered", -2); otherPlace.Location = "Ward 13";
        var otherIssue = Row("Registered", -2); otherIssue.SubcategoryCode = "low_pressure";
        var otherDepartment = Row("Registered", -2); otherDepartment.Department = "roads";
        var result = AnalyticsService.Recurrences(Snapshot(a, b, otherPlace, otherIssue, otherDepartment),
            [Work(b, Now, Now.AddDays(-1))], Now).Single();
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.Open);
        Assert.Equal(1, result.Reopened);
        Assert.Equal(new[] { a.Id, b.Id }, result.CaseIds);
    }

    [Fact]
    public void RecurrenceAfterClosureRequiresStrictlyLaterCreationAndValidClosure()
    {
        var first = Row("Closed", -8); first.ClosedUtc = Now.AddDays(-5);
        var before = Row("Registered", -6);
        var equal = Row("Registered", -5);
        var after = Row("Registered", -4);
        var later = Row("Closed", -3); later.ClosedUtc = Now.AddDays(-2);
        var last = Row("Registered", -1);
        var result = AnalyticsService.Recurrences(Snapshot(first, before, equal, after, later, last), [], Now).Single();
        Assert.Equal(6, result.Count);
        Assert.Equal(3, result.AfterClosure); // Each later complaint counted once, even with multiple earlier closures.
        Assert.Equal(4, result.Open);
    }

    [Fact]
    public void InvalidClosureAndReopenRecordsNeverCreateRecurrenceEvidence()
    {
        var first = Row("Closed", -5); first.ClosedUtc = Now.AddDays(-6);
        var future = Row("Closed", -4); future.ClosedUtc = Now.AddDays(1);
        var open = Row("InProgress", -3); open.ClosedUtc = Now.AddDays(-2);
        var last = Row("Registered", -1);
        var result = AnalyticsService.Recurrences(Snapshot(first, future, open, last),
            [Work(last, Now, Now.AddDays(-2)), Work(first, Now, Now.AddDays(1))], Now).Single();
        Assert.Equal(0, result.AfterClosure);
        Assert.Equal(0, result.Reopened);
    }

    [Theory]
    [InlineData("district")]
    [InlineData("location")]
    [InlineData("issue")]
    [InlineData("review")]
    [InlineData("draft")]
    public void IncompleteOrUnclassifiedCasesDoNotFormRecurringGroups(string excluded)
    {
        var a = Row("Registered", -2); var b = Row("Registered", -1);
        foreach (var row in new[] { a, b })
        {
            if (excluded == "district") row.District = " ";
            if (excluded == "location") row.Location = " ";
            if (excluded == "issue") row.SubcategoryCode = "";
            if (excluded == "review") row.Department = "review";
            if (excluded == "draft") row.Status = "Draft";
        }
        Assert.Empty(AnalyticsService.Recurrences(Snapshot(a, b), [], Now));
    }

    [Fact]
    public void CreationWindowAndSampleFiltersApplyBeforeRecurrenceComparison()
    {
        var old = Row("Closed", -40); old.ClosedUtc = Now.AddDays(-35);
        var current = Row("Registered", -2);
        var sample = Row("Registered", -1); sample.IsSample = true;
        var rows = new[] { old, current, sample };
        var report = AnalyticsService.Summarize(rows, new AnalyticsFilter(), Now);
        Assert.Equal(1, AnalyticsService.Performance(report, [], Now).Single().Total);
        Assert.Empty(AnalyticsService.Recurrences(report, [], Now));
        var included = AnalyticsService.Recurrences(AnalyticsService.Summarize(rows, new AnalyticsFilter(IncludeSamples: true), Now), [], Now).Single();
        Assert.Equal(2, included.Count);
        Assert.Equal(0, included.AfterClosure);
        var all = AnalyticsService.Recurrences(AnalyticsService.Summarize(rows, new AnalyticsFilter("all", IncludeSamples: true), Now), [], Now).Single();
        Assert.Equal(2, all.AfterClosure);
    }

    [Fact]
    public async Task DatabaseLoadPopulatesBothReportsWithoutForeignOwnerCasesOrWorkflows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var owner = new Operator { Email = "performance@example.test", BootstrapSlot = 1 };
        var other = new Operator { Email = "other-performance@example.test", BootstrapSlot = 2 };
        db.Operators.AddRange(owner, other);
        var mine = Row("Registered", -2); mine.OwnerId = owner.Id;
        var mine2 = Row("InProgress", -1); mine2.OwnerId = owner.Id;
        var foreign = Row("Closed", -3); foreign.OwnerId = other.Id; foreign.ClosedUtc = Now.AddDays(-2);
        db.Grievances.AddRange(mine, mine2, foreign);
        var mineWork = Work(mine2, Now.AddDays(1)); mineWork.AssignedOfficerId = owner.Id;
        var foreignWork = Work(foreign, Now.AddDays(-1), Now.AddDays(-1)); foreignWork.AssignedOfficerId = other.Id;
        db.CaseWorkflows.AddRange(mineWork, foreignWork);
        await db.SaveChangesAsync();
        var result = await new AnalyticsService(db).LoadAsync(owner.Id, new AnalyticsFilter(), Now);
        Assert.Equal(2, result.PerformanceRows.Single().Total);
        Assert.Equal(1, result.PerformanceRows.Single().Unassigned);
        Assert.Equal(0, result.PerformanceRows.Single().Reopened);
        Assert.Equal(0, result.RecurringRows.Single().AfterClosure);
        Assert.DoesNotContain(foreign.Id, result.RecurringRows.Single().CaseIds);
    }

    [Fact]
    public async Task AnalyticsPageShowsRecurringPlacesOnlyWithinSignedInWorkspace()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var html = await client.GetStringAsync("/Setup");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var setup = await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Name"] = "Performance Admin", ["Input.Email"] = "performance-admin@example.test",
            ["Input.Password"] = "Performance-Test-Password-72!", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            var owner = await db.Operators.SingleAsync(o => o.Email == "performance-admin@example.test");
            var other = new Operator { Email = "foreign-performance@example.test", BootstrapSlot = 2 };
            db.Operators.Add(other);
            foreach (var ownerId in new[] { owner.Id, other.Id })
                for (var i = 0; i < 2; i++)
                {
                    var row = Row("Registered", -1); row.CreatedUtc = DateTime.UtcNow.AddDays(-1);
                    row.OwnerId = ownerId; row.Location = ownerId == owner.Id ? "Visible recurring locality" : "Foreign secret locality";
                    db.Grievances.Add(row);
                }
            await db.SaveChangesAsync();
        }
        var response = await client.GetAsync("/Analytics?Period=all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Visible recurring locality", page);
        Assert.DoesNotContain("Foreign secret locality", page);
        var filtered = await client.GetStringAsync("/Analytics?Period=all&Department=roads");
        Assert.DoesNotContain("Visible recurring locality", filtered);
    }

    private static AnalyticsSnapshot Snapshot(params Grievance[] rows) => AnalyticsService.Summarize(rows, new AnalyticsFilter("all"), Now);
    private static Grievance Row(string status, int days) => new()
    {
        Reference = Guid.NewGuid().ToString("N")[..20], Title = "Performance test complaint", Status = status,
        CreatedUtc = Now.AddDays(days), Department = "water", District = "Mysuru", Location = "Ward 12",
        CategoryCode = "water_supply", SubcategoryCode = "no_supply", Source = "Text"
    };
    private static CaseWorkflow Work(Grievance item, DateTime due, DateTime? reopened = null) => new()
    {
        Id = item.Id, AssignedOfficerId = Guid.NewGuid(), DueUtc = due, ReopenedUtc = reopened
    };
}
