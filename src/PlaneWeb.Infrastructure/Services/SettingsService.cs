using System.Collections.Concurrent;
using PlaneWeb.Core;
using PlaneWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>
/// Loads/saves settings per user (null user = the shared default), caches them, and notifies listeners on change.
/// A user without a row starts from a copy of the default.
/// </summary>
public sealed class SettingsService(IDbContextFactory<PlaneWebDbContext> dbFactory)
{
    private readonly ConcurrentDictionary<string, WallSettings> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const string DefaultKey = "";

    /// <summary>Raised after a save with the owner's user id (null for the default) and the new settings.</summary>
    public event Action<string?, WallSettings>? Changed;

    public async Task<WallSettings> GetAsync(string? userId = null, CancellationToken ct = default)
    {
        var key = userId ?? DefaultKey;
        if (_cache.TryGetValue(key, out var hit)) return Clone(hit);
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out hit)) return Clone(hit);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.Settings.AsNoTracking().Where(x => x.UserId == userId).FirstOrDefaultAsync(ct);
            if (row is null)
            {
                var template = userId is null ? null
                    : await db.Settings.AsNoTracking().Where(x => x.UserId == null).FirstOrDefaultAsync(ct);
                row = template is null ? new WallSettings() : Clone(template);
                row.Id = 0;
                row.UserId = userId;
                db.Settings.Add(row);
                await db.SaveChangesAsync(ct);
            }
            _cache[key] = row;
            return Clone(row);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Settings for every user plus the default (used to restore trails with the longest window).</summary>
    public async Task<IReadOnlyList<WallSettings>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.AsNoTracking().ToListAsync(ct);
    }

    public async Task SaveAsync(WallSettings s, CancellationToken ct = default) => await SaveAsync(null, s, ct);

    public async Task SaveAsync(string? userId, WallSettings s, CancellationToken ct = default)
    {
        s.UserId = userId;
        s.TrackedFlights = s.TrackedFlights
            .Select(CallsignNormalizer.Normalize).Where(x => x.Length > 0)
            .Distinct().Take(WallSettings.MaxTracked).ToList();
        s.RadiusNm = Math.Clamp(s.RadiusNm, 1, WallSettings.MaxQueryRadiusNm);
        // Persist the shape that will actually be used: a polygon needs at least 3 points.
        if (s.Shape == AreaShape.Polygon && s.Polygon.Count < 3) s.Shape = AreaShape.Radius;
        if (s.Validate() is { } error) throw new ArgumentException(error, nameof(s));
        s.RotateSeconds = Math.Clamp(s.RotateSeconds, 3, 120);
        s.MaxAreaFlights = Math.Clamp(s.MaxAreaFlights, 1, 20);
        s.TrailMinutes = Math.Clamp(s.TrailMinutes, 1, WallSettings.MaxTrailMinutes);
        if (!WallSettings.MapLayers.Contains(s.MapLayer)) s.MapLayer = WallSettings.MapLayers[0];

        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Settings.Where(x => x.UserId == userId).FirstOrDefaultAsync(ct);
            if (existing is null)
            {
                var row = Clone(s);
                row.Id = 0;
                db.Settings.Add(row);
                await db.SaveChangesAsync(ct);
                s.Id = row.Id;
            }
            else
            {
                s.Id = existing.Id; // never let a caller retarget another user's row
                db.Entry(existing).CurrentValues.SetValues(s);
                existing.TrackedFlights = s.TrackedFlights.ToList();
                await db.SaveChangesAsync(ct);
            }
            _cache[userId ?? DefaultKey] = Clone(s);
        }
        finally { _lock.Release(); }
        Changed?.Invoke(userId, Clone(s));
    }

    /// <summary>Drops a user's cached settings (e.g. after the account is deleted).</summary>
    public void Forget(string userId) => _cache.TryRemove(userId, out _);

    internal static WallSettings Clone(WallSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<WallSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
}
