using PlaneWeb.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlaneWeb.Infrastructure.Services;

public sealed class PlaneWebOptions
{
    public int PollSeconds { get; set; } = 5;
    /// <summary>Where flight trails are saved between restarts.</summary>
    public string TrailsFile { get; set; } = "trails.json";
    /// <summary>Minimum gap between history (trace) requests to adsb.lol.</summary>
    public int TraceMinIntervalMs { get; set; } = 2000;
}

/// <summary>Holds the latest snapshot and notifies UI components.</summary>
public sealed class FlightStateStore
{
    private WallSnapshot _current = WallSnapshot.Empty;
    public WallSnapshot Current => _current;
    public event Action<WallSnapshot>? Updated;

    public void Publish(WallSnapshot s)
    {
        _current = s;
        Updated?.Invoke(s);
    }
}

/// <summary>Polls the feed for one set of settings and records positions into the shared trail store.</summary>
public sealed class FlightPollingService(
    IFlightDataProvider provider,
    IRouteLookup routes,
    TrailStore trails,
    TraceBackfillService backfill,
    TimeProvider time)
{
    /// <summary>Polls once and records every mapped aircraft's position into the trail store.</summary>
    public async Task<WallSnapshot> PollAndRecordAsync(WallSettings s, CancellationToken ct)
    {
        var snap = await PollAsync(s, ct);
        // Positions are stamped with when they were fetched, not when enrichment finished, so a slower poll
        // of an overlapping area can't record older coordinates after newer ones (TrailStore rejects them).
        var observed = snap.UpdatedAt;
        foreach (var a in snap.MapAircraft)
        {
            trails.Record(a, observed);
            // Enqueue skips hexes already tried, so this also retries ones a full queue rejected.
            if (s.TraceBackfill) backfill.Enqueue(a.Hex);
        }
        trails.Prune(time.GetUtcNow());
        return snap;
    }

    public async Task<WallSnapshot> PollAsync(WallSettings s, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (s.Mode == DisplayMode.Area)
        {
            var (center, radius) = s.Shape == AreaShape.Polygon && s.Polygon.Count >= 3
                ? Geo.BoundingCircle(s.Polygon)
                : (s.Center, s.RadiusNm);
            var raw = await provider.GetAircraftNearAsync(center, radius, ct);
            var observedAt = time.GetUtcNow();
            var inArea = FlightProcessor.FilterArea(raw.Aircraft, s)
                .OrderBy(a => Geo.DistanceNm(s.Center, a.Position!.Value))
                .ToList();
            var filtered = inArea.Take(Math.Max(s.MaxAreaFlights * 3, 20)).ToList();
            var views = await Task.WhenAll(filtered.Select(async a =>
            {
                var r = a.Callsign is { } cs ? await routes.GetRouteAsync(cs, ct) : null;
                return FlightProcessor.Enrich(a, r, s.Center, now);
            }));
            return new WallSnapshot
            {
                Mode = DisplayMode.Area, UpdatedAt = observedAt, Source = raw.Source,
                AreaFlights = views.OrderBy(v => v.DistanceNm).ToList(),
                MapAircraft = inArea,
            };
        }

        var tracked = await Task.WhenAll(s.TrackedFlights.Take(WallSettings.MaxTracked).Select(async req =>
        {
            var cs = CallsignNormalizer.Normalize(req);
            var result = await provider.GetByCallsignAsync(cs, ct);
            var at = time.GetUtcNow();
            var found = result.Aircraft
                .Where(a => string.Equals(a.Callsign, cs, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Position.HasValue)
                .FirstOrDefault();
            if (found is null) return (View: new TrackedFlightView(req, cs, null), result.Source, At: at);
            var route = await routes.GetRouteAsync(cs, ct);
            return (View: new TrackedFlightView(req, cs, FlightProcessor.Enrich(found, route, s.Center, now)), result.Source, At: at);
        }));
        // Report every provider that contributed to this snapshot (fallback may differ per request).
        var sources = tracked.Select(t => t.Source).Distinct().ToList();
        return new WallSnapshot
        {
            // Earliest fetch time: conservative stamp for every position in this snapshot.
            Mode = DisplayMode.Flights, UpdatedAt = tracked.Length == 0 ? time.GetUtcNow() : tracked.Min(t => t.At),
            Source = sources.Count == 0 ? null : string.Join(" + ", sources),
            Tracked = tracked.Select(t => t.View).ToList(),
            MapAircraft = tracked.Select(t => t.View.Live?.Aircraft).OfType<Aircraft>().Where(a => a.Position.HasValue).ToList(),
        };
    }
}
