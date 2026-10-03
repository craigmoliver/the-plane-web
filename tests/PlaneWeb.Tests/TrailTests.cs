using PlaneWeb.Core;
using PlaneWeb.Infrastructure.Providers;
using PlaneWeb.Infrastructure.Services;

namespace PlaneWeb.Tests;

public class TraceParserTests
{
    // Trimmed real response from adsb.lol /data/traces/4e/trace_recent_acf84e.json (plus a synthetic ground row).
    internal const string Sample = """
    {"icao": "acf84e", "r": "N935AT", "t": "B712", "desc": "BOEING 717-200", "timestamp": 1791061630.532, "trace": [
     [362.09, 35.590118, -86.222363, 33000, 462.7, 169.5, 0, 0, null, "adsb_icao", 35000, null, null, null],
     [382.02, 35.548233, -86.213093, 33000, 462.3, 169.8, 0, 64, null, "adsb_icao", 35000, null, null, null],
     [396.55, 35.517588, -86.206094, 33000, 463.8, 168.8, 0, -64, null, "adsb_icao", 35000, null, null, null],
     [1202.13, 34.136197, -84.962311, 12950, 392.0, 143.0, 0, -512, null, "adsb_icao", 13700, null, null, null],
     [1204.91, 34.132233, -84.958677, 12925, 392.0, 143.0, 0, -256, null, "adsb_icao", 13675, null, null, null],
     [1217.47, 34.114225, -84.94194, 12900, 391.1, 142.0, 0, 0, {"flight": "DAL2763 "}, "adsb_icao", 13650, null, null, null],
     [1222.47, 34.0, -84.5, "ground", 0, null, 0, null, null, "adsb_icao", null, null, null, null],
     ["bad"], [1, null, 2, 3]]}
    """;

    [Fact]
    public void ParsesTracePointsWithAbsoluteTimes()
    {
        var pts = TraceParser.Parse(Sample);
        Assert.Equal(7, pts.Count);
        Assert.Equal(1791061630.532 + 362.09, pts[0].T, 3);
        Assert.Equal(35.590118, pts[0].Lat);
        Assert.Equal(33000, pts[0].AltFt);
        Assert.Equal(0, pts[^1].AltFt); // "ground"
        Assert.True(pts.Zip(pts.Skip(1)).All(p => p.First.T < p.Second.T));
    }

    [Fact]
    public void ReturnsEmptyForUnexpectedShape() =>
        Assert.Empty(TraceParser.Parse("""{"icao":"x"}"""));

    [Fact]
    public async Task ClientBuildsTracePathAndRejectsBadHex()
    {
        var h = new Capture(Sample);
        var c = new AdsbLolTraceClient(new HttpClient(h) { BaseAddress = new Uri("https://adsb.lol/") });
        Assert.Equal(7, (await c.GetRecentAsync("ACF84E", default)).Count);
        Assert.Equal("/data/traces/4e/trace_recent_acf84e.json", h.LastPath);
        Assert.Empty(await c.GetRecentAsync("../x", default));
        Assert.Equal(1, h.Calls);
    }

    internal sealed class Capture(string body) : HttpMessageHandler
    {
        public int Calls;
        public string? LastPath;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastPath = r.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

public class TrailStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
    private static Aircraft Ac(string hex, double lat, double lon, int? alt = 10000) =>
        new() { Hex = hex, Lat = lat, Lon = lon, AltitudeFt = alt };

    [Fact]
    public void Record_ReportsNewHex_AndSkipsRepeatedPositions()
    {
        var s = new TrailStore();
        Assert.True(s.Record(Ac("a", 34, -84), T0));
        Assert.False(s.Record(Ac("a", 34, -84), T0.AddSeconds(5)));      // same position
        Assert.False(s.Record(Ac("a", 34.01, -84), T0.AddSeconds(10)));
        Assert.False(s.Record(new Aircraft { Hex = "nopos" }, T0));        // no position: ignored
        Assert.Equal(2, s.Get("a")!.Points.Count);
        Assert.Null(s.Get("nopos"));
    }

    [Fact]
    public void Prune_TrimsToWindow_AndDropsStaleAircraft()
    {
        var s = new TrailStore { Window = TimeSpan.FromMinutes(15) };
        for (var i = 0; i <= 20; i++) s.Record(Ac("a", 34 + i * 0.01, -84), T0.AddMinutes(i));
        s.Record(Ac("gone", 34, -84), T0);
        s.Prune(T0.AddMinutes(20));

        var a = s.Get("a")!;
        Assert.Equal(16, a.Points.Count); // minutes 5..20
        Assert.True(a.Points[0].T >= T0.AddMinutes(5).ToUnixTimeSeconds());
        Assert.Null(s.Get("gone"));       // not seen for 20 min > 5 min stale limit
    }

    [Fact]
    public void Merge_AddsOlderHistoryInOrder_DedupesAndBumpsRevision()
    {
        var s = new TrailStore();
        s.Record(Ac("a", 34.5, -84), T0);
        var rev = s.Get("a")!.Revision;
        var t = T0.ToUnixTimeSeconds();
        s.Merge("a",
        [
            new TrailPoint(t - 7200, 30, -80, 30000),   // outside 15 min window: dropped
            new TrailPoint(t - 120, 34.1, -84, 9000),
            new TrailPoint(t - 60, 34.3, -84, 9500),
            new TrailPoint(t - 0.4, 34.5, -84, 10000),  // duplicate of live point
        ], T0);

        var trail = s.Get("a")!;
        Assert.Equal([34.1, 34.3, 34.5], trail.Points.Select(p => p.Lat));
        Assert.True(trail.Revision > rev);

        s.Merge("unknown", [new TrailPoint(t, 1, 1, 1)], T0); // aircraft not tracked: ignored
        Assert.Null(s.Get("unknown"));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips_AndPrunesOnLoad()
    {
        var s = new TrailStore();
        s.Record(Ac("a", 34, -84, alt: null), T0);
        s.Record(Ac("a", 34.1, -84, alt: 12000), T0.AddSeconds(10));
        s.Record(Ac("old", 34, -84), T0.AddMinutes(-30));

        var path = Path.Combine(Path.GetTempPath(), $"fw-trails-{Guid.NewGuid()}", "trails.json");
        await s.SaveAsync(path);
        Assert.False(File.Exists(path + ".tmp"));

        var loaded = new TrailStore();
        Assert.Equal(1, loaded.Load(await File.ReadAllTextAsync(path), T0.AddMinutes(1)));
        var a = loaded.Get("a")!;
        Assert.Equal(2, a.Points.Count);
        Assert.Null(a.Points[0].AltFt);
        Assert.Equal(12000, a.Points[1].AltFt);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class TrailRecordingTests
{
    private sealed class Fixed(params Aircraft[] list) : IFlightDataProvider
    {
        public string Name => "fixed";
        public Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double r, CancellationToken ct) => Task.FromResult(new ProviderResult(list, Name));
        public Task<ProviderResult> GetByCallsignAsync(string cs, CancellationToken ct) => Task.FromResult(new ProviderResult([], Name));
    }
    private sealed class NoRoutes : IRouteLookup
    {
        public Task<FlightRoute?> GetRouteAsync(string cs, CancellationToken ct) => Task.FromResult<FlightRoute?>(null);
    }

    [Fact]
    public async Task Poll_RecordsEveryAircraftInArea_AndBackfillsNewOnes()
    {
        // 25 aircraft in range: more than the wall's 20 cap, all must reach the map and trail store.
        var aircraft = Enumerable.Range(0, 25)
            .Select(i => new Aircraft { Hex = $"a{i:x4}", Lat = 34.1 + i * 0.001, Lon = -84.5, AltitudeFt = 10000 })
            .Append(new Aircraft { Hex = "far", Lat = 40, Lon = -84.5, AltitudeFt = 10000 })
            .ToArray();
        var opt = Microsoft.Extensions.Options.Options.Create(new PlaneWebOptions { TraceMinIntervalMs = 250 });
        var trails = new TrailStore();
        var handler = new TraceParserTests.Capture("""{"timestamp": 0, "trace": []}""");
        var backfill = new TraceBackfillService(
            new AdsbLolTraceClient(new HttpClient(handler) { BaseAddress = new Uri("https://adsb.lol/") }),
            trails, opt, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TraceBackfillService>.Instance);
        var svc = new FlightPollingService(new Fixed(aircraft), new NoRoutes(), null!, new FlightStateStore(), trails, backfill,
            opt, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<FlightPollingService>.Instance);
        var settings = new WallSettings { CenterLat = 34.1, CenterLon = -84.5, RadiusNm = 10, TrailMinutes = 15 };

        var snap = await svc.PollAndRecordAsync(settings, default);
        Assert.Equal(20, snap.AreaFlights.Count);
        Assert.Equal(25, snap.MapAircraft.Count);
        Assert.Equal(25, trails.Count);
        Assert.Null(trails.Get("far"));

        // Already-attempted hexes are not queued again.
        Assert.False(backfill.Enqueue("a0000"));
        Assert.True(backfill.Enqueue("new1"));
    }

    [Fact]
    public void Enqueue_FullQueue_IsRejectedAndRetryable()
    {
        var opt = Microsoft.Extensions.Options.Options.Create(new PlaneWebOptions());
        var backfill = new TraceBackfillService(
            new AdsbLolTraceClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }),
            new TrailStore(), opt, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TraceBackfillService>.Instance);
        for (var i = 0; i < 500; i++) Assert.True(backfill.Enqueue($"h{i}"));
        Assert.False(backfill.Enqueue("overflow"));   // queue full: not recorded as attempted
        Assert.False(backfill.Enqueue("h0"));         // already queued
    }
}
