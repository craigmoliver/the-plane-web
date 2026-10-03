using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FlightWall.Infrastructure.Data;

/// <summary>Used by `dotnet ef` at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FlightWallDbContext>
{
    public FlightWallDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FlightWallDbContext>().UseSqlite("Data Source=design.db").Options);
}
