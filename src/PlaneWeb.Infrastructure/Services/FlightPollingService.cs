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

public sealed class FlightPollingService(
    IFlightDataProvider provider,
    IRouteLookup routes,
    SettingsService settings,
    FlightStateStore store,
    TrailStore trails,
    TraceBackfillService backfill,
    IOptions<PlaneWebOptions> options,
    TimeProvider time,
    ILogger<FlightPollingService> log) : BackgroundService
{
    /// <summary>Polls once and records every mapped aircraft's position into the trail store.</summary>
    public async Task<WallSnapshot> PollAndRecordAsync(WallSettings s, CancellationToken ct)
    {
        backfill.Enabled = s.TraceBackfill; // before polling, so an outage can't delay disabling it
        var snap = await PollAsync(s, ct);
        var now = time.GetUtcNow();
        trails.Window = TimeSpan.FromMinutes(Math.Clamp(s.TrailMinutes, 1, WallSettings.MaxTrailMinutes));
        foreach (var a in snap.MapAircraft)
        {
            trails.Record(a, now);
            // Enqueue skips hexes already tried, so this also retries ones a full queue rejected.
            if (s.TraceBackfill) backfill.Enqueue(a.Hex);
        }
        trails.Prune(now);
        return snap;
    }

    private CancellationTokenSource _wake = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Refresh immediately when settings change.
        settings.Changed += _ => { try { _wake.Cancel(); } catch (ObjectDisposedException) { } };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = await settings.GetAsync(stoppingToken);
                store.Publish(await PollAndRecordAsync(s, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Poll failed");
                // Keep the last successful UpdatedAt so stale data is not presented as fresh.
                store.Publish(store.Current with { Error = $"Live data unavailable (last attempt {time.GetUtcNow().ToLocalTime():HH:mm:ss})" });
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _wake.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, options.Value.PollSeconds)), time, linked.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _wake.Dispose();
                _wake = new CancellationTokenSource();
            }
        }
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
                Mode = DisplayMode.Area, UpdatedAt = now, Source = raw.Source,
                AreaFlights = views.OrderBy(v => v.DistanceNm).ToList(),
                MapAircraft = inArea,
            };
        }

        var tracked = await Task.WhenAll(s.TrackedFlights.Take(WallSettings.MaxTracked).Select(async req =>
        {
            var cs = CallsignNormalizer.Normalize(req);
            var result = await provider.GetByCallsignAsync(cs, ct);
            var found = result.Aircraft
                .Where(a => string.Equals(a.Callsign, cs, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Position.HasValue)
                .FirstOrDefault();
            if (found is null) return (View: new TrackedFlightView(req, cs, null), result.Source);
            var route = await routes.GetRouteAsync(cs, ct);
            return (View: new TrackedFlightView(req, cs, FlightProcessor.Enrich(found, route, s.Center, now)), result.Source);
        }));
        // Report every provider that contributed to this snapshot (fallback may differ per request).
        var sources = tracked.Select(t => t.Source).Distinct().ToList();
        return new WallSnapshot
        {
            Mode = DisplayMode.Flights, UpdatedAt = now,
            Source = sources.Count == 0 ? null : string.Join(" + ", sources),
            Tracked = tracked.Select(t => t.View).ToList(),
            MapAircraft = tracked.Select(t => t.View.Live?.Aircraft).OfType<Aircraft>().Where(a => a.Position.HasValue).ToList(),
        };
    }
}
