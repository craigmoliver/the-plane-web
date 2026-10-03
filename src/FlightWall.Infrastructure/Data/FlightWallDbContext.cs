using System.Text.Json;
using FlightWall.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace FlightWall.Infrastructure.Data;

public sealed class FlightWallDbContext(DbContextOptions<FlightWallDbContext> options) : DbContext(options)
{
    public DbSet<WallSettings> Settings => Set<WallSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var e = b.Entity<WallSettings>();
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Ignore(x => x.Center);
        e.Ignore(x => x.Polygon);
        e.Property(x => x.TrackedFlights)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, c) => a!.SequenceEqual(c!),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                    v => v.ToList()));
    }
}
