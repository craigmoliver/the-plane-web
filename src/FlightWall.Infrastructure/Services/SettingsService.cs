using FlightWall.Core;
using FlightWall.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FlightWall.Infrastructure.Services;

/// <summary>Loads/saves the single settings row, caches it, and notifies listeners on change.</summary>
public sealed class SettingsService(IDbContextFactory<FlightWallDbContext> dbFactory)
{
    private WallSettings? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<WallSettings>? Changed;

    public async Task<WallSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return Clone(_cached);
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                _cached = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
                if (_cached is null)
                {
                    _cached = new WallSettings();
                    db.Settings.Add(_cached);
                    await db.SaveChangesAsync(ct);
                }
            }
            return Clone(_cached);
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(WallSettings s, CancellationToken ct = default)
    {
        s.Id = 1;
        s.TrackedFlights = s.TrackedFlights
            .Select(CallsignNormalizer.Normalize).Where(x => x.Length > 0)
            .Distinct().Take(WallSettings.MaxTracked).ToList();
        s.RadiusNm = Math.Clamp(s.RadiusNm, 1, WallSettings.MaxQueryRadiusNm);
        // Persist the shape that will actually be used: a polygon needs at least 3 points.
        if (s.Shape == AreaShape.Polygon && s.Polygon.Count < 3) s.Shape = AreaShape.Radius;
        if (s.Validate() is { } error) throw new ArgumentException(error, nameof(s));
        s.RotateSeconds = Math.Clamp(s.RotateSeconds, 3, 120);
        s.MaxAreaFlights = Math.Clamp(s.MaxAreaFlights, 1, 20);

        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Settings.FirstOrDefaultAsync(ct);
            if (existing is null) db.Settings.Add(Clone(s));
            else
            {
                db.Entry(existing).CurrentValues.SetValues(s);
                existing.TrackedFlights = s.TrackedFlights.ToList();
            }
            await db.SaveChangesAsync(ct);
            _cached = Clone(s);
        }
        finally { _lock.Release(); }
        Changed?.Invoke(Clone(s));
    }

    private static WallSettings Clone(WallSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<WallSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
}
