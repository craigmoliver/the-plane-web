using System.Collections.Concurrent;
using PlaneWeb.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>A page's hold on a running poller. Dispose when the page closes or its settings change.</summary>
public sealed class PollerLease : IDisposable
{
    private readonly Action _release;
    private int _disposed;
    internal PollerLease(FlightStateStore store, Action release) { Store = store; _release = release; }
    public FlightStateStore Store { get; }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _release(); }
}

/// <summary>
/// Runs one poller per distinct set of poll-relevant settings, shared by everyone viewing that area.
/// Pollers stop a short while after their last viewer leaves; the shared default area is always polled
/// so trails keep building with nobody watching.
/// </summary>
public sealed class PollerRegistry(
    FlightPollingService poller,
    SettingsService settings,
    TrailStore trails,
    TraceBackfillService backfill,
    IOptions<PlaneWebOptions> options,
    TimeProvider time,
    ILogger<PollerRegistry> log) : BackgroundService
{
    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(2);

    private sealed class Poller
    {
        public required string Key;
        public required WallSettings Settings;
        public readonly FlightStateStore Store = new();
        public readonly CancellationTokenSource Cts = new();
        public int Leases;
        public bool Pinned;
        public DateTimeOffset LastReleased;
    }

    private readonly ConcurrentDictionary<string, Poller> _pollers = new();
    private readonly Lock _lock = new();
    private CancellationToken _stopping = CancellationToken.None;
    private string? _pinnedKey;

    public int ActiveCount => _pollers.Count;

    /// <summary>Identity of a poll: everything that changes what is fetched or filtered.</summary>
    public static string KeyFor(WallSettings s) => System.Text.Json.JsonSerializer.Serialize(new
    {
        s.Mode, s.Shape, s.CenterLat, s.CenterLon, s.RadiusNm, s.PolygonJson,
        Tracked = s.Mode == DisplayMode.Flights ? s.TrackedFlights : [],
        s.MinAltitudeFt, s.MaxAltitudeFt, s.IncludeGround, s.IncludeHelicopters, s.IncludeLight,
        s.MaxAreaFlights, s.TraceBackfill, s.TrailMinutes,
    });

    public PollerLease Acquire(WallSettings s)
    {
        Poller p;
        // One critical section with Sweep, so a poller can't be removed between lookup and lease.
        lock (_lock) { p = GetOrStart(s); p.Leases++; }
        return new PollerLease(p.Store, () =>
        {
            lock (_lock) { p.Leases--; p.LastReleased = time.GetUtcNow(); }
        });
    }

    /// <summary>The live aircraft with this hex in any running area, if any.</summary>
    public Aircraft? FindLive(string hex) =>
        _pollers.Values.SelectMany(p => p.Store.Current.MapAircraft)
            .FirstOrDefault(a => string.Equals(a.Hex, hex, StringComparison.OrdinalIgnoreCase));

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private Poller GetOrStart(WallSettings s)
    {
        var key = KeyFor(s);
        if (_pollers.TryGetValue(key, out var existing)) return existing;
        var p = new Poller { Key = key, Settings = SettingsService.Clone(s), LastReleased = time.GetUtcNow() };
        _pollers[key] = p;
        ApplySharedSettings();
        _ = Task.Run(() => RunAsync(p));
        return p;
    }

    /// <summary>History lookups run if any active area wants them; trails keep the longest active window.</summary>
    private void ApplySharedSettings()
    {
        var active = _pollers.Values.Select(p => p.Settings).ToList();
        backfill.Enabled = active.Any(s => s.TraceBackfill);
        if (active.Count > 0)
            trails.Window = TimeSpan.FromMinutes(Math.Clamp(active.Max(s => s.TrailMinutes), 1, WallSettings.MaxTrailMinutes));
    }

    private async Task RunAsync(Poller p)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(p.Cts.Token, _stopping);
        var ct = linked.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                p.Store.Publish(await poller.PollAndRecordAsync(p.Settings, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Poll failed");
                // Keep the last successful UpdatedAt so stale data is not presented as fresh.
                p.Store.Publish(p.Store.Current with { Error = $"Live data unavailable (last attempt {time.GetUtcNow().ToLocalTime():HH:mm:ss})" });
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, options.Value.PollSeconds)), time, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Pin(WallSettings s)
    {
        lock (_lock)
        {
            var p = GetOrStart(s);
            if (_pinnedKey is { } old && old != p.Key && _pollers.TryGetValue(old, out var prev))
            {
                prev.Pinned = false;
                prev.LastReleased = time.GetUtcNow();
            }
            p.Pinned = true;
            _pinnedKey = p.Key;
        }
    }

    /// <summary>Stops pollers nobody has used for <see cref="Linger"/>. Public for tests.</summary>
    public void Sweep()
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            foreach (var p in _pollers.Values.ToList())
            {
                if (p.Pinned || p.Leases > 0 || now - p.LastReleased < Linger) continue;
                _pollers.TryRemove(p.Key, out _);
                p.Cts.Cancel();
                p.Cts.Dispose();
            }
            ApplySharedSettings();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        void OnChanged(string? userId, WallSettings s) { if (userId is null) Pin(s); }
        settings.Changed += OnChanged;
        try
        {
            Pin(await settings.GetAsync(null, stoppingToken));
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
            while (await timer.WaitForNextTickAsync(stoppingToken)) Sweep();
        }
        catch (OperationCanceledException) { }
        finally
        {
            settings.Changed -= OnChanged;
            foreach (var p in _pollers.Values) p.Cts.Cancel();
        }
    }
}
