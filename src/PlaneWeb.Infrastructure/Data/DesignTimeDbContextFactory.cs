using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlaneWeb.Infrastructure.Data;

// Used by `dotnet ef`; pick the context with --context. No database connection is needed to add migrations.
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqlitePlaneWebDbContext>
{
    public SqlitePlaneWebDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlitePlaneWebDbContext>().UseSqlite("Data Source=design.db").Options);
}

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresPlaneWebDbContext>
{
    public PostgresPlaneWebDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresPlaneWebDbContext>().UseNpgsql("Host=localhost;Database=design").Options);
}
