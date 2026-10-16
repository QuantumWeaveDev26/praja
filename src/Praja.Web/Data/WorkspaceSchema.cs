using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.RegularExpressions;

namespace Praja.Web.Data;

public static class WorkspaceSchema
{
    private static readonly SemaphoreSlim Gate = new(1,1);
    public static async Task InitializeAsync(WorkspaceDb db,string backupDirectory,CancellationToken cancellation=default)
    {
        if(!db.Database.IsSqlite())
        {
            // SQL Server retains the existing fresh-prototype path; provider-specific
            // production migration scripts still require a reviewed deployment.
            await db.Database.EnsureCreatedAsync(cancellation); return;
        }
        await Gate.WaitAsync(cancellation);
        var connection=(SqliteConnection)db.Database.GetDbConnection();
        var opened=connection.State!=System.Data.ConnectionState.Open;
        try
        {
            if(opened)await connection.OpenAsync(cancellation);
            var migrations=db.Database.GetMigrations().ToArray();
            var baseline=migrations.Single(m=>m.EndsWith("_LegacyBaseline",StringComparison.Ordinal));
            var applied=(await db.Database.GetAppliedMigrationsAsync(cancellation)).ToArray();
            if(applied.Any(m=>!migrations.Contains(m)))throw new InvalidOperationException("This workspace has an unknown migration history. Preserve it and review the schema before upgrading.");
            var pending=(await db.Database.GetPendingMigrationsAsync(cancellation)).ToArray();
            if(pending.Length==0)return;
            var existing=await Fingerprint(connection,cancellation);
            var adoptLegacy=applied.Length==0 && existing.Count>0;
            if(adoptLegacy)
            {
                await using var reference=new SqliteConnection("Data Source=:memory:"); await reference.OpenAsync(cancellation);
                using(var create=reference.CreateCommand())
                {
                    create.CommandText=db.GetService<IMigrator>().GenerateScript("0",baseline,MigrationsSqlGenerationOptions.NoTransactions);
                    await create.ExecuteNonQueryAsync(cancellation);
                }
                var expected=await Fingerprint(reference,cancellation);
                if(!existing.SequenceEqual(expected))throw new InvalidOperationException("The existing database does not match the legacy workspace schema. No schema changes were applied.");
                using var integrity=connection.CreateCommand(); integrity.CommandText="PRAGMA foreign_key_check";
                await using var problems=await integrity.ExecuteReaderAsync(cancellation);
                if(await problems.ReadAsync(cancellation))throw new InvalidOperationException("Existing database relationships need repair before this upgrade. No schema changes were applied.");
            }
            if(existing.Count>0)
            {
                Directory.CreateDirectory(backupDirectory);
                var path=Path.Combine(Path.GetFullPath(backupDirectory),"before-schema-upgrade-"+Guid.NewGuid().ToString("N")+".db");
                await using var destination=new SqliteConnection($"Data Source={path};Pooling=False"); await destination.OpenAsync(cancellation);
                connection.BackupDatabase(destination);
            }
            if(adoptLegacy)
            {
                await using var transaction=await db.Database.BeginTransactionAsync(cancellation);
                var history=db.GetService<IHistoryRepository>();
                await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(),cancellation);
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baseline,"10.0.12")),cancellation);
                await transaction.CommitAsync(cancellation);
            }
            await db.Database.MigrateAsync(cancellation);
        }
        finally { if(opened)await connection.CloseAsync(); Gate.Release(); }
    }
    private static async Task<List<string>> Fingerprint(SqliteConnection connection,CancellationToken cancellation)
    {
        using var command=connection.CreateCommand();
        command.CommandText="SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND tbl_name NOT IN ('__EFMigrationsHistory','__EFMigrationsLock') ORDER BY type,name";
        await using var rows=await command.ExecuteReaderAsync(cancellation); var result=new List<string>();
        while(await rows.ReadAsync(cancellation))result.Add(rows.GetString(0)+"|"+rows.GetString(1)+"|"+Regex.Replace(rows.GetString(2),"\\s+"," ").Trim().TrimEnd(';'));
        return result;
    }
}
