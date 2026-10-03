using FlightWall.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightWall.Infrastructure.Services;

public sealed class FlightWallOptions
{
    public int PollSeconds { get; set; } = 5;
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
    IOptions<FlightWallOptions> options,
    TimeProvider time,
    ILogger<FlightPollingService> log) : BackgroundService
{
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
                store.Publish(await PollAsync(s, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Poll failed");
                store.Publish(store.Current with { Error = "Live data unavailable", UpdatedAt = time.GetUtcNow() });
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
            var filtered = FlightProcessor.FilterArea(raw, s)
                .OrderBy(a => Geo.DistanceNm(s.Center, a.Position!.Value))
                .Take(Math.Max(s.MaxAreaFlights * 3, 20))
                .ToList();
            var views = await Task.WhenAll(filtered.Select(async a =>
            {
                var r = a.Callsign is { } cs ? await routes.GetRouteAsync(cs, ct) : null;
                return FlightProcessor.Enrich(a, r, s.Center, now);
            }));
            return new WallSnapshot
            {
                Mode = DisplayMode.Area, UpdatedAt = now, Source = provider.Name,
                AreaFlights = views.OrderBy(v => v.DistanceNm).ToList(),
            };
        }

        var tracked = await Task.WhenAll(s.TrackedFlights.Take(WallSettings.MaxTracked).Select(async req =>
        {
            var cs = CallsignNormalizer.Normalize(req);
            var found = (await provider.GetByCallsignAsync(cs, ct))
                .Where(a => string.Equals(a.Callsign, cs, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Position.HasValue)
                .FirstOrDefault();
            if (found is null) return new TrackedFlightView(req, cs, null);
            var route = await routes.GetRouteAsync(cs, ct);
            return new TrackedFlightView(req, cs, FlightProcessor.Enrich(found, route, s.Center, now));
        }));
        return new WallSnapshot { Mode = DisplayMode.Flights, UpdatedAt = now, Source = provider.Name, Tracked = tracked };
    }
}
