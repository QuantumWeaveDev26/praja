using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class AnalyticsTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SummaryUsesRecordedClosureDurationsAndExcludesSamplesByDefault()
    {
        var rows = new[]
        {
            Row("Closed", Now.AddDays(-4), Now.AddDays(-3)),
            Row("Closed", Now.AddDays(-3), Now.AddDays(-1)),
            Row("InProgress", Now.AddDays(-1)),
            Row("Closed", Now.AddDays(-2), Now, sample: true),
            Row("Closed", Now.AddDays(-2)), // No usable closure timestamp.
            Row("Closed", Now.AddDays(-1), Now.AddDays(-2)) // Invalid negative duration.
        };
        var result = AnalyticsService.Summarize(rows, new AnalyticsFilter(), Now);
        Assert.Equal(5, result.Volume);
        Assert.Equal(1, result.Open);
        Assert.Equal(4, result.Closed);
        Assert.Equal(36d, result.AverageResolutionHours);
        Assert.Equal(2, result.TimedClosures);
        Assert.Equal(5, result.Departments.Single().Volume);
        Assert.Equal(4, result.DailyTrend.Count);
    }

    [Fact]
    public void FiltersUseCreationDateUtcAndCombineDistrictDepartmentAndSamples()
    {
        var rows = new[]
        {
            Row("Draft", new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc), district: "Mysuru"),
            Row("Draft", new DateTime(2026, 10, 5, 23, 59, 59, DateTimeKind.Utc), district: "Mysuru"),
            Row("Draft", Now, district: "Bengaluru"),
            Row("Draft", Now, department: "roads", district: "Mysuru"),
            Row("Draft", Now, district: "Mysuru", sample: true),
            Row("Draft", Now.AddDays(1), district: "Mysuru")
        };
        var filter = new AnalyticsFilter("7", "Mysuru", "water");
        Assert.Equal(1, AnalyticsService.Summarize(rows, filter, Now).Volume);
        var included = AnalyticsService.Summarize(rows, filter with { IncludeSamples = true }, Now);
        Assert.Equal(2, included.Volume);
        Assert.Equal(1, included.SampleCount);
        Assert.Equal(5, AnalyticsService.Summarize(rows, new AnalyticsFilter("all", IncludeSamples: true), Now).Volume);
    }

    [Fact]
    public void EmptySummaryAndBadFiltersAreHandledWithoutInventingMetrics()
    {
        var result = AnalyticsService.Summarize([], new AnalyticsFilter(), Now);
        Assert.Equal(0, result.Volume);
        Assert.Null(result.AverageResolutionHours);
        Assert.Empty(result.Departments);
        Assert.Throws<ArgumentException>(() => AnalyticsService.Summarize([], new AnalyticsFilter("nonsense"), Now));
        Assert.Throws<ArgumentException>(() => AnalyticsService.Summarize([], new AnalyticsFilter(Department: "water' OR 1=1"), Now));
        Assert.Throws<ArgumentException>(() => AnalyticsService.Summarize([], new AnalyticsFilter(District: new string('x', 101)), Now));
    }

    [Theory]
    [InlineData("30", 0)]
    [InlineData("90", 1)]
    public void ThirtyAndNinetyDayWindowsKeepTheirOwnCreationBoundaries(string period, int expected)
        => Assert.Equal(expected, AnalyticsService.Summarize([Row("Draft", Now.AddDays(-45))], new AnalyticsFilter(period), Now).Volume);

    [Fact]
    public void DepartmentDistrictAndSourceGroupsUseIndependentCounts()
    {
        var first = Row("Closed", Now.AddDays(-1), Now, district: "Mandya");
        var second = Row("Draft", Now, district: "Mandya", department: "roads");
        second.Source = "Paper transcription";
        var third = Row("Registered", Now);
        var result = AnalyticsService.Summarize([first, second, third], new AnalyticsFilter(), Now);
        Assert.Equal(new AnalyticsGroup("Water supply", 2, 1, 1), result.Departments[0]);
        Assert.Equal(new AnalyticsGroup("Roads & public works", 1, 1, 0), result.Departments[1]);
        Assert.Equal(new AnalyticsGroup("Mandya", 2, 1, 1), result.Districts[0]);
        Assert.Equal(new AnalyticsGroup("Mysuru", 1, 1, 0), result.Districts[1]);
        Assert.Equal(new AnalyticsGroup("Text", 2, 1, 1), result.Sources[0]);
        Assert.Equal(new AnalyticsGroup("Paper transcription", 1, 1, 0), result.Sources[1]);
        Assert.Equal(1, result.DailyTrend[0].Volume);
        Assert.Equal(2, result.DailyTrend[1].Volume);
    }

    [Fact]
    public void CountsClosureChecklistFlagsOnlyForCasesAwaitingClosureReview()
    {
        var pending = Row("ClosureReview", Now);
        pending.OriginalText = "A pending application needs review.";
        var draft = Row("Draft", Now);
        var result = AnalyticsService.Summarize([pending, draft], new AnalyticsFilter(), Now);
        Assert.Equal(1, result.FlaggedClosureCases);
        Assert.Equal(3, result.ClosureFlagCount);
        Assert.Equal(3, result.ClosureFlags.Count);
    }

    [Theory]
    [InlineData("=SUM(1,2)", "\"'=SUM(1,2)\"")]
    [InlineData("  +cmd", "\"'  +cmd\"")]
    [InlineData("-10", "\"'-10\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tplain", "\"'\tplain\"")]
    [InlineData(" \r\nplain", "\"' \r\nplain\"")]
    [InlineData("safe, \"quoted\"", "\"safe, \"\"quoted\"\"\"")]
    [InlineData("safe value", "\"safe value\"")]
    public void CsvCellsQuoteTextAndNeutralizeFormulaPrefixes(string value, string expected)
        => Assert.Equal(expected, AnalyticsService.CsvCell(value));

    [Fact]
    public void CsvExportsOnlyFilteredMinimalFieldsWithoutNarrativeOrContactInformation()
    {
        var row = Row("Draft", Now);
        row.Reference = "REF-1";
        row.Title = "=HYPERLINK(\"bad\")";
        row.OriginalText = "SECRET CITIZEN NARRATIVE";
        row.Location = "PRIVATE LOCATION";
        var csv = AnalyticsService.ExportCsv(AnalyticsService.Summarize([row], new AnalyticsFilter(), Now));
        Assert.StartsWith("Reference,Title,District,Department,Status,CreatedUtc,ClosedUtc,IsSample\r\n", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"bad\"\")\"", csv);
        Assert.DoesNotContain("SECRET", csv);
        Assert.DoesNotContain("PRIVATE", csv);
        Assert.Contains("2026-10-12T12:00:00.0000000Z", csv);
    }

    [Fact]
    public async Task DatabaseQueryNeverIncludesAnotherOwnersRecordsOrDistrictOptions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options;
        await using var db = new WorkspaceDb(options);
        await db.Database.EnsureCreatedAsync();
        var owner = new Operator { Email = "one@example.test", BootstrapSlot = 1 };
        var other = new Operator { Email = "two@example.test", BootstrapSlot = 2 };
        db.Operators.AddRange(owner, other);
        var mine = Row("Draft", Now, district: "Mysuru");
        mine.OwnerId = owner.Id;
        var theirs = Row("Closed", Now, district: "Private district");
        theirs.OwnerId = other.Id;
        db.Grievances.AddRange(mine, theirs);
        await db.SaveChangesAsync();
        var service = new AnalyticsService(db);
        var result = await service.LoadAsync(owner.Id, new AnalyticsFilter(), Now);
        Assert.Equal(1, result.Volume);
        Assert.Equal(mine.Id, result.Rows.Single().Id);
        Assert.Equal(new[] { "Mysuru" }, await service.DistrictOptionsAsync(owner.Id, false));
    }

    [Fact]
    public async Task AnonymousUsersCannotReadAnalyticsOrDownloadCsv()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var path in new[] { "/Analytics", "/Analytics?handler=Export" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/Account/Login", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task AuthenticatedExportsRespectFiltersOwnerAndExplicitSampleInclusion()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var setupHtml = await client.GetStringAsync("/Setup");
        var token = Regex.Match(setupHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var setup = await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Name"] = "Analytics Officer", ["Input.Email"] = "analytics@example.test",
            ["Input.Password"] = "Analytics-Workspace-Password-72!", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
            var owner = await db.Operators.SingleAsync(o => o.Email == "analytics@example.test");
            var other = new Operator { Email = "private@example.test", BootstrapSlot = 2 };
            db.Operators.Add(other);
            var mine = Row("Draft", DateTime.UtcNow.AddDays(-1));
            mine.OwnerId = owner.Id; mine.Reference = "MY-REAL-CASE";
            var sample = Row("Draft", DateTime.UtcNow.AddDays(-1), sample: true);
            sample.OwnerId = owner.Id; sample.Reference = "MY-FICTIONAL-CASE";
            var theirs = Row("Closed", DateTime.UtcNow.AddDays(-1), district: "Secret district");
            theirs.OwnerId = other.Id; theirs.Reference = "ANOTHER-OWNERS-CASE";
            db.Grievances.AddRange(mine, sample, theirs);
            await db.SaveChangesAsync();
        }
        var page = await client.GetAsync("/Analytics");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("Secret district", await page.Content.ReadAsStringAsync());
        var realExport = await client.GetAsync("/Analytics?handler=Export&Period=all");
        Assert.Equal(HttpStatusCode.OK, realExport.StatusCode);
        Assert.Equal("text/csv", realExport.Content.Headers.ContentType!.MediaType);
        Assert.Equal("praja-local-analytics.csv", realExport.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var realCsv = await realExport.Content.ReadAsStringAsync();
        Assert.Contains("MY-REAL-CASE", realCsv);
        Assert.DoesNotContain("FICTIONAL", realCsv);
        Assert.DoesNotContain("ANOTHER", realCsv);
        var sampleCsv = await client.GetStringAsync("/Analytics?handler=Export&Period=all&IncludeSamples=true");
        Assert.Contains("MY-FICTIONAL-CASE", sampleCsv);
        Assert.DoesNotContain("ANOTHER", sampleCsv);
        var filteredCsv = await client.GetStringAsync("/Analytics?handler=Export&Period=all&Department=roads");
        Assert.DoesNotContain("MY-REAL-CASE", filteredCsv);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/Analytics?handler=Export&Period=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Analytics?Period=invalid")).StatusCode);
        var samplePage = await client.GetStringAsync("/Analytics?IncludeSamples=true");
        Assert.Contains("Fictional sample data is included.", samplePage);
    }

    private static Grievance Row(string status, DateTime created, DateTime? closed = null,
        string district = "Mysuru", string department = "water", bool sample = false) => new()
    {
        Reference = Guid.NewGuid().ToString("N")[..20], Title = "Recorded grievance",
        Status = status, CreatedUtc = created, ClosedUtc = closed, District = district,
        Department = department, Source = "Text", IsSample = sample
    };
}
