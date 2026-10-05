using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PlaneWeb.Core;

namespace PlaneWeb.Infrastructure.Data;

/// <summary>A signed-in person. Local accounts have a password; work (Entra) accounts sign in via Microsoft.</summary>
public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }
    /// <summary>Set when an admin creates or resets a local account; cleared after the user picks a new password.</summary>
    public bool MustChangePassword { get; set; }
    /// <summary>True for accounts created from a Microsoft (Entra ID) sign-in.</summary>
    public bool IsExternal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

/// <summary>
/// An email allowed to sign in with Google, managed on <c>/admin/google-allowlist</c>. Seeded once from
/// <c>PlaneWeb:Auth:Google</c> config on first startup; changes after that only happen here.
/// </summary>
public sealed class GoogleAllowedUser
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public bool IsAdmin { get; set; }
    public string? AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// Shared model. Concrete subclasses exist per database provider so each has its own migrations
/// (SQLite for the home server, PostgreSQL for Azure).
/// </summary>
public abstract class PlaneWebDbContext(DbContextOptions options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<WallSettings> Settings => Set<WallSettings>();
    public DbSet<GoogleAllowedUser> GoogleAllowedUsers => Set<GoogleAllowedUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        var g = b.Entity<GoogleAllowedUser>();
        g.HasIndex(x => x.Email).IsUnique();
        g.Property(x => x.Email).HasMaxLength(320);

        var e = b.Entity<WallSettings>();
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedOnAdd();
        // One row per user; the row with a null UserId is the shared default new users start from.
        e.HasIndex(x => x.UserId).IsUnique();
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
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

public sealed class SqlitePlaneWebDbContext(DbContextOptions<SqlitePlaneWebDbContext> options) : PlaneWebDbContext(options);

public sealed class PostgresPlaneWebDbContext(DbContextOptions<PostgresPlaneWebDbContext> options) : PlaneWebDbContext(options);
