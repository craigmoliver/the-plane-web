using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlaneWeb.Infrastructure.Data;

/// <summary>Used by `dotnet ef` at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlaneWebDbContext>
{
    public PlaneWebDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PlaneWebDbContext>().UseSqlite("Data Source=design.db").Options);
}
