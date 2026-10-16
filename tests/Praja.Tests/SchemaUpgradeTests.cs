using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Praja.Web.Data;
using Xunit;

namespace Praja.Tests;

public class SchemaUpgradeTests
{
    [Fact]
    public async Task RoutingUpgradePreservesInboxSourceEvidenceAndExistingClassificationState()
    {
        var path=Path.Combine(Path.GetTempPath(),"praja-routing-upgrade-"+Guid.NewGuid().ToString("N")+".db");var backups=path+".backups";
        try{
            await using var db=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite($"Data Source={path};Pooling=False").Options);
            var old=db.Database.GetMigrations().Single(m=>m.EndsWith("_AddIntakeInbox"));await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(old);
            var owner=new Operator{Email="routing-upgrade@example.test",PasswordHash="original-hash"};await InsertOldOperator(db,owner);
            var item=new Grievance{OwnerId=owner.Id,Reference="PRE-ROUTING",OriginalText="Keep this original source exactly.",DraftText="Keep the edited draft exactly.",Department="water",Version=7};await InsertOldCase(db,item);
            db.InboxMessages.Add(new(){OwnerId=owner.Id,OriginalText=item.OriginalText,Channel="Text",GrievanceId=item.Id,State="Converted",Version=2,CapturedBy="Original officer"});
            db.EvidenceAttachments.Add(new(){GrievanceId=item.Id,FileName="evidence.pdf",ContentType="application/pdf",Sha256=new string('B',64),ByteLength=3,ProtectedContent=[4,5,6]});await db.SaveChangesAsync();
            await WorkspaceSchema.InitializeAsync(db,backups);db.ChangeTracker.Clear();var saved=await db.Grievances.SingleAsync();
            Assert.Equal(item.OriginalText,saved.OriginalText);Assert.Equal(item.DraftText,saved.DraftText);Assert.Equal(7,saved.Version);Assert.Equal("water",saved.Department);Assert.Equal("",saved.CategoryCode);Assert.Equal("",saved.RoutingMethod);
            var inbox=await db.InboxMessages.SingleAsync();Assert.Equal(item.Id,inbox.GrievanceId);Assert.Equal(2,inbox.Version);Assert.Equal(item.OriginalText,inbox.OriginalText);Assert.Equal(new byte[]{4,5,6},(await db.EvidenceAttachments.SingleAsync()).ProtectedContent);
            await WorkspaceSchema.InitializeAsync(db,backups);Assert.Single(Directory.GetFiles(backups,"*.db"));Assert.Equal(db.Database.GetMigrations().Count(),(await db.Database.GetAppliedMigrationsAsync()).Count());
        }finally{if(File.Exists(path))File.Delete(path);if(Directory.Exists(backups)&&Path.GetFullPath(backups).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(backups,true);}
    }
    [Fact]
    public async Task InboxUpgradePreservesExistingEvidenceAndCaseData()
    {
        var path=Path.Combine(Path.GetTempPath(),"praja-inbox-upgrade-"+Guid.NewGuid().ToString("N")+".db");var backups=path+".backups";
        try{
            await using var db=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite($"Data Source={path};Pooling=False").Options);
            var old=db.Database.GetMigrations().Single(m=>m.EndsWith("_AddEvidenceAttachments"));
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(old);
            var owner=new Operator{Email="pre-inbox@example.test",PasswordHash="preserve-password",FailedLogins=3};
            var item=new Grievance{OwnerId=owner.Id,Reference="PRE-INBOX",OriginalText="Original prior complaint",Version=7};
            var evidence=new EvidenceAttachment{GrievanceId=item.Id,FileName="retained.pdf",ContentType="application/pdf",Sha256=new string('A',64),ByteLength=12,ProtectedContent=[1,2,3,4]};
            await InsertOldOperator(db,owner);await InsertOldCase(db,item);db.EvidenceAttachments.Add(evidence);await db.SaveChangesAsync();
            await WorkspaceSchema.InitializeAsync(db,backups);db.ChangeTracker.Clear();
            Assert.Equal("preserve-password",await db.Operators.Select(o=>o.PasswordHash).SingleAsync());Assert.Equal(7,(await db.Grievances.SingleAsync()).Version);
            Assert.Equal(new byte[]{1,2,3,4},(await db.EvidenceAttachments.SingleAsync()).ProtectedContent);Assert.Empty(await db.InboxMessages.ToListAsync());
            await WorkspaceSchema.InitializeAsync(db,backups);Assert.Single(Directory.GetFiles(backups,"*.db"));
        }finally{if(File.Exists(path))File.Delete(path);if(Directory.Exists(backups)&&Path.GetFullPath(backups).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(backups,true);}
    }
    [Fact]
    public async Task LegacyUpgradePreservesOriginalRecordsAndIsIdempotent()
    {
        var path=Path.Combine(Path.GetTempPath(),"praja-upgrade-"+Guid.NewGuid().ToString("N")+".db");
        var backups=path+".backups";
        try
        {
            await using var db=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await CreateUntrackedBaseline(db);
            var owner=new Operator {Email="legacy@example.test",Name="Original officer",PasswordHash="original-hash",FailedLogins=2};
            var item=new Grievance {OwnerId=owner.Id,Reference="LEGACY-PRESERVED",OriginalText="Original legacy statement",DraftText="Reviewed draft",Status="ClosureReview",Version=9,EvidenceReference="EXISTING-REF"};
            item.History.Add(new() {Actor="Original officer",Action="Existing audit entry"});
            await InsertOldOperator(db,owner); await InsertOldCase(db,item);
            foreach(var audit in item.History){audit.GrievanceId=item.Id;db.AuditEntries.Add(audit);}await db.SaveChangesAsync();
            await WorkspaceSchema.InitializeAsync(db,backups);
            db.ChangeTracker.Clear();
            Assert.Equal("original-hash",await db.Operators.Select(o=>o.PasswordHash).SingleAsync());
            Assert.Equal(2,(await db.Operators.SingleAsync()).FailedLogins);
            var saved=await db.Grievances.Include(g=>g.History).SingleAsync();
            Assert.Equal(item.Id,saved.Id); Assert.Equal("Original legacy statement",saved.OriginalText); Assert.Equal("Reviewed draft",saved.DraftText); Assert.Equal(9,saved.Version);
            Assert.Equal("EXISTING-REF",saved.EvidenceReference); Assert.Single(saved.History); Assert.Empty(await db.EvidenceAttachments.ToListAsync());
            Assert.Equal(db.Database.GetMigrations().Count(),(await db.Database.GetAppliedMigrationsAsync()).Count()); Assert.Single(Directory.GetFiles(backups,"*.db"));
            Assert.Equal("",saved.CategoryCode);Assert.Equal("",saved.SubcategoryCode);Assert.Equal("",saved.RoutingEvidence);
            await WorkspaceSchema.InitializeAsync(db,backups);
            Assert.Single(Directory.GetFiles(backups,"*.db")); Assert.Equal(1,await db.Grievances.CountAsync());
            var backup=Directory.GetFiles(backups,"*.db").Single();
            await using var copy=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite($"Data Source={backup};Mode=ReadOnly;Pooling=False").Options);
            Assert.Equal("original-hash",await copy.Operators.Select(o=>o.PasswordHash).SingleAsync());
            await copy.Database.OpenConnectionAsync();using var restored=copy.Database.GetDbConnection().CreateCommand();restored.CommandText="SELECT Reference FROM Grievances";Assert.Equal("LEGACY-PRESERVED",await restored.ExecuteScalarAsync());
        }
        finally
        {
            if(File.Exists(path))File.Delete(path);
            if(Directory.Exists(backups) && Path.GetFullPath(backups).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(backups,true);
        }
    }
    [Fact]
    public async Task UnknownLegacySchemaIsRejectedWithoutChangingTables()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE UnexpectedData (Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO UnexpectedData VALUES (1,'keep');");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkspaceSchema.InitializeAsync(db,Path.GetTempPath()));
        using var command=connection.CreateCommand(); command.CommandText="SELECT Value FROM UnexpectedData WHERE Id=1"; Assert.Equal("keep",await command.ExecuteScalarAsync());
        command.CommandText="SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsHistory'"; Assert.Equal(0L,await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task BackupFailurePreventsBaselineAdoptionAndKeepsRows()
    {
        var path=Path.Combine(Path.GetTempPath(),"praja-backup-failure-"+Guid.NewGuid().ToString("N")+".db"); var blocker=path+".blocker";
        try
        {
            await using var db=new WorkspaceDb(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await CreateUntrackedBaseline(db);
            var owner=new Operator {Email="backup@example.test",PasswordHash="keep-hash"}; await InsertOldOperator(db,owner);
            await File.WriteAllTextAsync(blocker,"blocks directory creation");
            await Assert.ThrowsAsync<IOException>(()=>WorkspaceSchema.InitializeAsync(db,blocker));
            Assert.Equal("keep-hash",await db.Operators.Select(o=>o.PasswordHash).SingleAsync()); Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        }
        finally {if(File.Exists(path))File.Delete(path);if(File.Exists(blocker))File.Delete(blocker);}
    }
    private static async Task CreateUntrackedBaseline(WorkspaceDb db)
    {
        var baseline=db.Database.GetMigrations().Single(m=>m.EndsWith("_LegacyBaseline"));
        await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(baseline);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory");
    }
    // Seed through the actual older schema; current EF inserts require the new columns.
    private static Task InsertOldCase(WorkspaceDb db,Grievance item)=>InsertOld(db,item,"Grievances");
    private static Task InsertOldOperator(WorkspaceDb db,Operator item)=>InsertOld(db,item,"Operators");
    private static async Task InsertOld<T>(WorkspaceDb db,T item,string table)
    {
        await db.Database.OpenConnectionAsync();var connection=db.Database.GetDbConnection();var columns=new List<string>();
        using(var schema=connection.CreateCommand()){
            schema.CommandText="PRAGMA table_info("+table+")";await using var rows=await schema.ExecuteReaderAsync();while(await rows.ReadAsync())columns.Add(rows.GetString(1));
        }
        using var insert=connection.CreateCommand();insert.CommandText="INSERT INTO "+table+" ("+string.Join(",",columns.Select(c=>"\""+c+"\""))+") VALUES ("+string.Join(",",columns.Select((_,i)=>"$p"+i))+")";
        for(var i=0;i<columns.Count;i++){var parameter=insert.CreateParameter();parameter.ParameterName="$p"+i;parameter.Value=typeof(T).GetProperty(columns[i])!.GetValue(item)??DBNull.Value;insert.Parameters.Add(parameter);}
        await insert.ExecuteNonQueryAsync();
    }
}
