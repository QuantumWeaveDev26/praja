using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Praja.Web.Data;

// Migration generation uses a design-only SQLite path; it never starts the web app.
public sealed class WorkspaceDbDesignFactory : IDesignTimeDbContextFactory<WorkspaceDb>
{
    public WorkspaceDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<WorkspaceDb>().UseSqlite("Data Source=praja-design-only.db").Options);
}
