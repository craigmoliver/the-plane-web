using Microsoft.AspNetCore.Identity;
using PlaneWeb.Infrastructure.Data;

namespace PlaneWeb.Web;

/// <summary>
/// Hash of a random password, verified whenever a sign-in attempt does no real password check (no matching
/// account, or an already-locked one), so every failure costs about as much as a wrong password. Created at
/// startup with the configured hasher, so no sign-in request pays the extra cost of hashing it.
/// </summary>
public sealed class DummyPasswordHash
{
    private volatile string? _value;

    public bool IsInitialized => _value is not null;

    /// <summary>Called once at startup (see <see cref="AuthSetup.InitializeAuthAsync"/>).</summary>
    public void Initialize(IPasswordHasher<AppUser> hasher) => _value = Create(hasher);

    /// <summary>The hash; created on first use only if startup didn't (e.g. a host that skipped initialization).</summary>
    public string Get(IPasswordHasher<AppUser> hasher) => _value ??= Create(hasher);

    private static string Create(IPasswordHasher<AppUser> hasher) =>
        hasher.HashPassword(new AppUser(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
}
