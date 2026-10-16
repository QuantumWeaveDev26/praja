using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;
using Xunit;

namespace Praja.Tests;

public class EvidenceTests
{
    private static readonly byte[] Pdf = "%PDF-1.7\nSynthetic evidence document for an inspection.\n%%EOF"u8.ToArray();
    [Fact]
    public async Task UploadEncryptsBytesAuditsChangeAndDownloadsOriginalOnlyForOwner()
    {
        await using var fixture = await Fixture.Create();
        var saved = await fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",1,File(Pdf,"../../inspection.pdf"),"Inspection record",true);
        var row = await fixture.Db.EvidenceAttachments.SingleAsync();
        Assert.Equal("inspection.pdf",saved.FileName); Assert.Equal(Pdf.Length,saved.ByteLength);
        Assert.False(row.ProtectedContent.AsSpan().SequenceEqual(Pdf));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Pdf)),saved.Sha256);
        Assert.Equal(2,fixture.Case.Version); Assert.Single(await fixture.Db.AuditEntries.ToListAsync());
        Assert.Equal(Pdf,(await fixture.Service.DownloadAsync(fixture.Case.Id,saved.Id,fixture.Case.OwnerId))!.Bytes);
        Assert.Null(await fixture.Service.DownloadAsync(fixture.Case.Id,saved.Id,Guid.NewGuid()));
        Assert.Empty(await fixture.Service.ListAsync(fixture.Case.Id,Guid.NewGuid()));
        row.ProtectedContent[20] ^= 1;
        fixture.Db.Entry(row).Property(e=>e.ProtectedContent).IsModified=true;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DownloadAsync(fixture.Case.Id,saved.Id,fixture.Case.OwnerId));
    }

    [Theory]
    [InlineData(false,"Registered",1,"inspection.pdf",true)]
    [InlineData(true,"Closed",1,"inspection.pdf",true)]
    [InlineData(true,"Draft",1,"inspection.pdf",true)]
    [InlineData(true,"Registered",0,"inspection.pdf",true)]
    [InlineData(true,"Registered",1,"inspection.exe",true)]
    [InlineData(true,"Registered",1,"inspection.pdf",false)]
    public async Task InvalidUploadLeavesCaseAndEvidenceUnchanged(bool consent,string status,int version,string name,bool signature)
    {
        await using var fixture = await Fixture.Create(); fixture.Case.Status=status; await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",version,File(signature?Pdf:"<script>not pdf</script>"u8.ToArray(),name),"",consent));
        Assert.Equal(1,fixture.Case.Version); Assert.Empty(await fixture.Db.EvidenceAttachments.ToListAsync()); Assert.Empty(await fixture.Db.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task DuplicateAndOverQuotaUploadsDoNotChangeHistory()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",1,File(Pdf,"first.pdf"),"",true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",2,File(Pdf,"duplicate.pdf"),"",true));
        for(var i=1;i<10;i++) await fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",i+1,File(System.Text.Encoding.UTF8.GetBytes($"%PDF-1.7\nSynthetic distinct record {i}\n%%EOF"),$"record{i}.pdf"),"",true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",11,File("%PDF-1.7\nOne more record\n%%EOF"u8.ToArray(),"eleventh.pdf"),"",true));
        Assert.Equal(10,await fixture.Db.EvidenceAttachments.CountAsync()); Assert.Equal(10,await fixture.Db.AuditEntries.CountAsync()); Assert.Equal(11,fixture.Case.Version);
    }

    [Fact]
    public async Task WrongOwnerCannotUploadAndOversizeFileIsRejected()
    {
        await using var fixture=await Fixture.Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>fixture.Service.UploadAsync(fixture.Case.Id,Guid.NewGuid(),"Test officer",1,File(Pdf,"x.pdf"),"",true));
        var huge=new byte[8*1024*1024+1]; Pdf.CopyTo(huge,0);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",1,File(huge,"large.pdf"),"",true));
        Assert.Empty(await fixture.Db.EvidenceAttachments.ToListAsync());
    }

    internal static IFormFile File(byte[] bytes,string name) => new FormFile(new MemoryStream(bytes),0,bytes.Length,"EvidenceFile",name);
    [Theory]
    [InlineData("image.png","89504E470D0A1A0A00010203")]
    [InlineData("image.jpeg","FFD8FF01020304")]
    public async Task SupportedImagesRoundTripWithoutChangingBytes(string name,string hex)
    {
        await using var fixture=await Fixture.Create(); var bytes=Convert.FromHexString(hex);
        var saved=await fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",1,File(bytes,name),"",true);
        Assert.Equal(bytes,(await fixture.Service.DownloadAsync(fixture.Case.Id,saved.Id,fixture.Case.OwnerId))!.Bytes);
    }
    [Fact]
    public async Task ExactEightMegabyteFileIsAcceptedAndRoundTrips()
    {
        await using var fixture=await Fixture.Create(); var bytes=new byte[8*1024*1024]; Pdf.CopyTo(bytes,0);
        var saved=await fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",1,File(bytes,"limit.pdf"),"",true);
        Assert.Equal(8*1024*1024,saved.ByteLength); Assert.Equal(bytes,(await fixture.Service.DownloadAsync(fixture.Case.Id,saved.Id,fixture.Case.OwnerId))!.Bytes);
    }
    [Fact]
    public async Task DatabaseSaveFailureRollsBackAttachmentAndAuditAndCaseVersion()
    {
        await using var fixture=await Fixture.Create(); fixture.Case.Title=null!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",1,File(Pdf,"inspection.pdf"),"",true));
        Assert.Empty(await fixture.Db.EvidenceAttachments.ToListAsync()); Assert.Empty(await fixture.Db.AuditEntries.ToListAsync());
        Assert.Equal(1,(await fixture.Db.Grievances.SingleAsync()).Version); Assert.Equal("",(await fixture.Db.Grievances.SingleAsync()).Title);
    }
    [Fact]
    public async Task CompetingCaseUpdateRollsBackAttachmentAndAuditTogether()
    {
        await using var fixture=await Fixture.Create();
        await using var competitor=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(fixture.Connection).Options);
        var changed=await competitor.Grievances.SingleAsync(); changed.Version=2; changed.Status="Closed"; await competitor.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>fixture.Service.UploadAsync(fixture.Case.Id,fixture.Case.OwnerId,"Test officer",1,File(Pdf,"inspection.pdf"),"",true));
        Assert.Empty(await fixture.Db.EvidenceAttachments.ToListAsync()); Assert.Empty(await fixture.Db.AuditEntries.ToListAsync());
        var saved=await fixture.Db.Grievances.SingleAsync(); Assert.Equal(2,saved.Version); Assert.Equal("Closed",saved.Status);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection {get;private set;}=null!;
        public WorkspaceDb Db {get;private set;}=null!;
        public Grievance Case {get;private set;}=null!;
        public EvidenceService Service {get;private set;}=null!;
        public static async Task<Fixture> Create()
        {
            var f=new Fixture {Connection=new SqliteConnection("Data Source=:memory:")}; await f.Connection.OpenAsync();
            f.Db=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(f.Connection).Options); await f.Db.Database.EnsureCreatedAsync();
            var owner=new Operator {Name="Test officer",Email="evidence@example.test"}; f.Db.Operators.Add(owner);
            f.Case=new Grievance {OwnerId=owner.Id,Reference="EVIDENCE-TEST",Status="Registered",OriginalText="No water supply in Sample Ward."}; f.Db.Grievances.Add(f.Case); await f.Db.SaveChangesAsync();
            f.Service=new(f.Db,new EphemeralDataProtectionProvider()); return f;
        }
        public async ValueTask DisposeAsync(){await Db.DisposeAsync();await Connection.DisposeAsync();}
    }
}
