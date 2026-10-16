using Praja.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Praja.Web.Services;

public static class LocalPasswordRecovery
{
    public static async Task<string> ResetAsync(WorkspaceDb db, IHostEnvironment environment, string password, string backupDirectory, CancellationToken cancellation = default)
    {
        if (!(environment.IsDevelopment() || environment.IsEnvironment("Testing")) || !db.Database.IsSqlite())
            throw new InvalidOperationException("This recovery command is only for the local SQLite pilot.");
        if (password.Length is < 14 or > 128 || password.Any(char.IsControl))
            throw new InvalidOperationException("Choose a password of 14–128 characters without control characters.");
        var users = await db.Operators.OrderBy(u => u.Id).Take(2).ToListAsync(cancellation);
        if (users.Count != 1) throw new InvalidOperationException("Recovery requires exactly one existing officer account.");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(Path.GetFullPath(backupDirectory), $"before-password-reset-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        // VACUUM INTO creates a consistent snapshot, including committed WAL data.
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", new object[] { backup }, cancellation);
        var user = users[0];
        user.PasswordHash = WorkspaceIdentity.Hasher.HashPassword(user, password);
        user.FailedLogins = 0; user.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellation);
        return user.Email;
    }
}
