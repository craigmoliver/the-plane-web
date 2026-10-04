using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlaneWeb.Core;
using PlaneWeb.Infrastructure.Data;
using PlaneWeb.Infrastructure.Providers;
using PlaneWeb.Infrastructure.Services;

namespace PlaneWeb.Tests;

/// <summary>In-memory SQLite database built from the real migrations.</summary>
internal sealed class TestDb : IDbContextFactory<PlaneWebDbContext>, IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<SqlitePlaneWebDbContext> _opts;

    public TestDb()
    {
        _conn.Open();
        _opts = new DbContextOptionsBuilder<SqlitePlaneWebDbContext>().UseSqlite(_conn).Options;
        using var db = new SqlitePlaneWebDbContext(_opts);
        db.Database.Migrate();
    }

    public PlaneWebDbContext CreateDbContext() => new SqlitePlaneWebDbContext(_opts);

    public async Task<string> AddUserAsync(string name)
    {
        await using var db = CreateDbContext();
        var u = new AppUser { UserName = name, NormalizedUserName = name.ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString() };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    public void Dispose() => _conn.Dispose();
}

public class SettingsServiceTests
{
    [Fact]
    public async Task Save_NormalizesPolygonWithTooFewPoints()
    {
        using var db = new TestDb();
        var svc = new SettingsService(db);
        await svc.SaveAsync(new WallSettings { Shape = AreaShape.Polygon, PolygonJson = "[[47,-122]]" });
        Assert.Equal(AreaShape.Radius, (await svc.GetAsync()).Shape);
    }

    [Fact]
    public async Task Save_RejectsOversizedPolygon()
    {
        using var db = new TestDb();
        var svc = new SettingsService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveAsync(new WallSettings
            { Shape = AreaShape.Polygon, PolygonJson = "[[30,-125],[30,-100],[49,-100],[49,-125]]" }));
    }

    [Fact]
    public async Task NewUser_StartsFromDefault_AndUsersAreIsolated()
    {
        using var db = new TestDb();
        var alice = await db.AddUserAsync("alice");
        var bob = await db.AddUserAsync("bob");
        var svc = new SettingsService(db);
        await svc.SaveAsync(null, new WallSettings { Title = "DEFAULT", RadiusNm = 30 });

        var a = await svc.GetAsync(alice);
        Assert.Equal("DEFAULT", a.Title); // copied from the shared default
        a.Title = "ALICE"; a.RadiusNm = 10;
        await svc.SaveAsync(alice, a);

        Assert.Equal("DEFAULT", (await svc.GetAsync(bob)).Title);
        Assert.Equal("DEFAULT", (await svc.GetAsync(null)).Title);
        // A fresh service (no cache) reads alice's own row back.
        Assert.Equal(10, (await new SettingsService(db).GetAsync(alice)).RadiusNm);
    }

    [Fact]
    public async Task Save_CannotOverwriteAnotherUsersRowById()
    {
        using var db = new TestDb();
        var alice = await db.AddUserAsync("alice");
        var bob = await db.AddUserAsync("bob");
        var svc = new SettingsService(db);
        var bobs = await svc.GetAsync(bob);
        var forged = await svc.GetAsync(alice);
        forged.Id = bobs.Id; // try to target bob's row
        forged.Title = "PWNED";
        await svc.SaveAsync(alice, forged);
        Assert.NotEqual("PWNED", (await new SettingsService(db).GetAsync(bob)).Title);
    }

    [Fact]
    public async Task Changed_ReportsOwner()
    {
        using var db = new TestDb();
        var alice = await db.AddUserAsync("alice");
        var svc = new SettingsService(db);
        string? owner = "unset";
        svc.Changed += (u, _) => owner = u;
        await svc.SaveAsync(alice, await svc.GetAsync(alice));
        Assert.Equal(alice, owner);
    }
}

public class PollerRegistryTests
{
    private sealed class Counting(bool fail = false) : IFlightDataProvider
    {
        public int Calls;
        public string Name => "test";
        public Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (fail) throw new HttpRequestException("down");
            return Task.FromResult(new ProviderResult([new Aircraft { Hex = $"h{c.Lat:0}", Lat = c.Lat, Lon = c.Lon, AltitudeFt = 5000 }], Name));
        }
        public Task<ProviderResult> GetByCallsignAsync(string cs, CancellationToken ct) => Task.FromResult(new ProviderResult([], Name));
    }
    private sealed class NoRoutes : IRouteLookup
    {
        public Task<FlightRoute?> GetRouteAsync(string cs, CancellationToken ct) => Task.FromResult<FlightRoute?>(null);
    }
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (PollerRegistry Reg, ManualTime Time, TraceBackfillService Backfill) Build(TestDb db, IFlightDataProvider provider) =>
        Build(db, provider, new TrailStore());

    private static (PollerRegistry Reg, ManualTime Time, TraceBackfillService Backfill) Build(TestDb db, IFlightDataProvider provider, TrailStore trails, int pollSeconds = 60)
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var opt = Options.Create(new PlaneWebOptions { PollSeconds = pollSeconds });
        var backfill = new TraceBackfillService(new AdsbLolTraceClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }),
            trails, opt, time, NullLogger<TraceBackfillService>.Instance);
        var poller = new FlightPollingService(provider, new NoRoutes(), trails, backfill, time);
        return (new PollerRegistry(poller, new SettingsService(db), trails, backfill, opt, time, NullLogger<PollerRegistry>.Instance), time, backfill);
    }

    private static async Task WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task SameArea_SharesOnePoller_DifferentAreasGetTheirOwn()
    {
        using var db = new TestDb();
        var provider = new Counting();
        var (reg, _, _) = Build(db, provider);
        using var a = reg.Acquire(new WallSettings { CenterLat = 34, CenterLon = -84 });
        using var b = reg.Acquire(new WallSettings { CenterLat = 34, CenterLon = -84, Title = "different title, same area" });
        using var c = reg.Acquire(new WallSettings { CenterLat = 40, CenterLon = -84 });

        Assert.Same(a.Store, b.Store);
        Assert.NotSame(a.Store, c.Store);
        Assert.Equal(2, reg.ActiveCount);
        await WaitFor(() => a.Store.Current.MapAircraft.Count == 1 && c.Store.Current.MapAircraft.Count == 1);
        Assert.Equal("h34", a.Store.Current.MapAircraft[0].Hex); // each area gets its own data
        Assert.Equal("h40", c.Store.Current.MapAircraft[0].Hex);
        Assert.NotNull(reg.FindLive("h40"));
    }

    [Fact]
    public async Task UnusedPoller_StopsAfterLinger()
    {
        using var db = new TestDb();
        var (reg, time, _) = Build(db, new Counting());
        var lease = reg.Acquire(new WallSettings());
        reg.Sweep();
        Assert.Equal(1, reg.ActiveCount);   // still leased
        lease.Dispose();
        lease.Dispose();                    // double dispose is harmless
        reg.Sweep();
        Assert.Equal(1, reg.ActiveCount);   // within the linger period
        time.Now += PollerRegistry.Linger + TimeSpan.FromSeconds(1);
        reg.Sweep();
        Assert.Equal(0, reg.ActiveCount);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FailedPoll_KeepsLastSuccessfulTimestamp()
    {
        using var db = new TestDb();
        var (reg, _, _) = Build(db, new Counting(fail: true));
        using var lease = reg.Acquire(new WallSettings());
        await WaitFor(() => lease.Store.Current.Error is not null);
        Assert.Equal(DateTimeOffset.MinValue, lease.Store.Current.UpdatedAt);
    }

    [Fact]
    public void HistoryLookups_OnlyWhileSomeActiveAreaWantsThem()
    {
        using var db = new TestDb();
        var (reg, time, backfill) = Build(db, new Counting());
        var on = reg.Acquire(new WallSettings { TraceBackfill = true });
        using var off = reg.Acquire(new WallSettings { TraceBackfill = false });
        Assert.True(backfill.Enabled);
        on.Dispose();
        time.Now += PollerRegistry.Linger + TimeSpan.FromSeconds(1);
        reg.Sweep();
        Assert.False(backfill.Enabled);
    }

    [Fact]
    public async Task SavedLongerTrailSetting_KeepsWindowWhileOwnerIsAway()
    {
        using var db = new TestDb();
        var alice = await db.AddUserAsync("alice");
        await new SettingsService(db).SaveAsync(alice, new WallSettings { TrailMinutes = 60 });
        var trails = new TrailStore();
        var (reg, _, _) = Build(db, new Counting(), trails);
        await reg.StartAsync(default);
        await WaitFor(() => trails.Window == TimeSpan.FromMinutes(60)); // default area is 15 min, alice saved 60
        await reg.StopAsync(default);
    }

    [Fact]
    public void IdlePollers_AreCappedWhenSettingsChangeRepeatedly()
    {
        using var db = new TestDb();
        var (reg, _, _) = Build(db, new Counting());
        for (var i = 0; i < PollerRegistry.MaxPollers + 10; i++)
            reg.Acquire(new WallSettings { CenterLat = 10 + i }).Dispose(); // each save -> new area, old one released
        Assert.True(reg.ActiveCount <= PollerRegistry.MaxPollers, $"{reg.ActiveCount} pollers");
        using var held = reg.Acquire(new WallSettings { CenterLat = 89 });
        Assert.True(reg.ActiveCount <= PollerRegistry.MaxPollers);
    }

    [Fact]
    public async Task FindLive_PrefersNewestSnapshot()
    {
        using var db = new TestDb();
        var (reg, time, _) = Build(db, new Counting());
        using var a = reg.Acquire(new WallSettings { CenterLat = 1 });
        using var b = reg.Acquire(new WallSettings { CenterLat = 2 });
        // Let both workers publish their first poll before seeding, so they can't overwrite the test values.
        await WaitFor(() => a.Store.Current.UpdatedAt != DateTimeOffset.MinValue && b.Store.Current.UpdatedAt != DateTimeOffset.MinValue);
        a.Store.Publish(new WallSnapshot { UpdatedAt = time.Now.AddMinutes(-1), MapAircraft = [new Aircraft { Hex = "x", Callsign = "OLD1" }] });
        b.Store.Publish(new WallSnapshot { UpdatedAt = time.Now, MapAircraft = [new Aircraft { Hex = "x", Callsign = "NEW1" }] });
        Assert.Equal("NEW1", reg.FindLive("x")!.Callsign);
    }

    private sealed class Sequenced : IFlightDataProvider
    {
        private int _n;
        public string Name => "seq";
        // Call 1 sees the aircraft at lat 34.00, call 2 (later) at 34.10.
        public Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double r, CancellationToken ct)
        {
            var lat = Interlocked.Increment(ref _n) == 1 ? 34.00 : 34.10;
            return Task.FromResult(new ProviderResult([new Aircraft { Hex = "abc", Callsign = "TST1", Lat = lat, Lon = -84.5, AltitudeFt = 9000 }], Name));
        }
        public Task<ProviderResult> GetByCallsignAsync(string cs, CancellationToken ct) => Task.FromResult(new ProviderResult([], Name));
    }

    private sealed class GatedRoutes : IRouteLookup
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _n;
        public async Task<FlightRoute?> GetRouteAsync(string cs, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _n) == 1) await Release.Task; // the first poll's enrichment is slow
            return null;
        }
    }

    [Fact]
    public async Task OverlappingPolls_CompletingOutOfOrder_KeepTrailOrder()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-04T12:00:00Z"));
        var trails = new TrailStore();
        var opt = Options.Create(new PlaneWebOptions());
        var backfill = new TraceBackfillService(new AdsbLolTraceClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }),
            trails, opt, time, NullLogger<TraceBackfillService>.Instance);
        var routes = new GatedRoutes();
        var poller = new FlightPollingService(new Sequenced(), routes, trails, backfill, time);
        var area = new WallSettings { CenterLat = 34.05, CenterLon = -84.5, RadiusNm = 50, TraceBackfill = false };

        var slow = poller.PollAndRecordAsync(area, default);            // fetches lat 34.00 at 12:00:00, then waits
        time.Now += TimeSpan.FromSeconds(5);
        await poller.PollAndRecordAsync(area, default);                 // fetches lat 34.10 at 12:00:05, records first
        time.Now += TimeSpan.FromSeconds(5);
        routes.Release.SetResult();
        await slow;                                                     // completes last with the older position

        var trail = trails.Get("abc")!;
        Assert.Equal([34.10], trail.Points.Select(p => p.Lat));        // older sample rejected, no doubling back
        Assert.Equal(DateTimeOffset.Parse("2026-10-04T12:00:05Z").ToUnixTimeSeconds(), trail.LastSeen, 3); // freshness not rewound
        trails.Prune(DateTimeOffset.Parse("2026-10-04T12:05:01Z"));    // 4m56s after the newest sighting
        Assert.NotNull(trails.Get("abc"));
    }

    private sealed class DelayedCallsigns(ManualTime time) : IFlightDataProvider
    {
        public readonly TaskCompletionSource ReleaseB = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "cs";
        public Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double r, CancellationToken ct) =>
            Task.FromResult(new ProviderResult([new Aircraft { Hex = "bbb", Callsign = "BBB2", Lat = 34.05, Lon = -84.5, AltitudeFt = 9000 }], Name));
        public async Task<ProviderResult> GetByCallsignAsync(string cs, CancellationToken ct)
        {
            if (cs == "BBB2") await ReleaseB.Task; // B's lookup returns later than A's
            var (hex, lat) = cs == "AAA1" ? ("aaa", 34.0) : ("bbb", 34.10);
            return new ProviderResult([new Aircraft { Hex = hex, Callsign = cs, Lat = lat, Lon = -84.5, AltitudeFt = 9000 }], Name);
        }
    }

    [Fact]
    public async Task TrackedFlights_KeepTheirOwnFetchTimes()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-04T12:00:00Z"));
        var trails = new TrailStore();
        var opt = Options.Create(new PlaneWebOptions());
        var backfill = new TraceBackfillService(new AdsbLolTraceClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }),
            trails, opt, time, NullLogger<TraceBackfillService>.Instance);
        var provider = new DelayedCallsigns(time);
        var poller = new FlightPollingService(provider, new NoRoutes(), trails, backfill, time);

        // A is fetched at t=0; B's lookup is still pending.
        var flights = poller.PollAndRecordAsync(new WallSettings { Mode = DisplayMode.Flights, TrackedFlights = ["AAA1", "BBB2"], TraceBackfill = false }, default);
        time.Now += TimeSpan.FromSeconds(5);
        // An overlapping area poll records B at t=5.
        await poller.PollAndRecordAsync(new WallSettings { CenterLat = 34.05, CenterLon = -84.5, RadiusNm = 20, TraceBackfill = false }, default);
        time.Now += TimeSpan.FromSeconds(5);
        provider.ReleaseB.SetResult();   // B is fetched at t=10 with a newer position
        await flights;

        Assert.Equal([34.05, 34.10], trails.Get("bbb")!.Points.Select(p => p.Lat)); // not rejected as if fetched at t=0
    }

    [Fact]
    public void TrailLength_DoesNotSplitPollers()
    {
        using var db = new TestDb();
        var (reg, _, _) = Build(db, new Counting());
        using var a = reg.Acquire(new WallSettings { TrailMinutes = 15 });
        using var b = reg.Acquire(new WallSettings { TrailMinutes = 60 });
        Assert.Same(a.Store, b.Store);
    }

    [Fact]
    public void PollKey_IgnoresInactiveFields()
    {
        var circleA = new WallSettings { Shape = AreaShape.Radius, PolygonJson = "[[1,1],[1,2],[2,2]]", TrackedFlights = ["X1"] };
        var circleB = new WallSettings { Shape = AreaShape.Radius, PolygonJson = "[]", TrackedFlights = [] };
        Assert.Equal(PollerRegistry.KeyFor(circleA), PollerRegistry.KeyFor(circleB));
        Assert.NotEqual(PollerRegistry.KeyFor(circleA), PollerRegistry.KeyFor(new WallSettings { RadiusNm = 5 }));
        var poly = new WallSettings { Shape = AreaShape.Polygon, PolygonJson = "[[34,-85],[34,-84],[35,-84]]" };
        Assert.Equal(PollerRegistry.KeyFor(poly), PollerRegistry.KeyFor(new WallSettings { Shape = AreaShape.Polygon, PolygonJson = poly.PolygonJson, RadiusNm = 99 }));
    }

    [Fact]
    public async Task FindLive_RanksByObservationTime()
    {
        using var db = new TestDb();
        var (reg, time, _) = Build(db, new Counting());
        using var flights = reg.Acquire(new WallSettings { CenterLat = 1 });
        using var area = reg.Acquire(new WallSettings { CenterLat = 2 });
        await WaitFor(() => flights.Store.Current.UpdatedAt != DateTimeOffset.MinValue && area.Store.Current.UpdatedAt != DateTimeOffset.MinValue);
        var t0 = time.Now;
        // Flights snapshot stamped t0 (earliest fetch) but contains B observed at t0+10.
        flights.Store.Publish(new WallSnapshot { UpdatedAt = t0, MapAircraft = [new Aircraft { Hex = "b", Callsign = "NEW1", ObservedAt = t0.AddSeconds(10) }] });
        area.Store.Publish(new WallSnapshot { UpdatedAt = t0.AddSeconds(5), MapAircraft = [new Aircraft { Hex = "b", Callsign = "OLD1", ObservedAt = t0.AddSeconds(5) }] });
        Assert.Equal("NEW1", reg.FindLive("b")!.Callsign);
    }

    [Fact]
    public void Record_RejectsSampleOlderThanNewestSighting_EvenAfterSkippedRepeats()
    {
        var t0 = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        var store = new TrailStore();
        store.Record(new Aircraft { Hex = "a", Lat = 34.0, Lon = -84 }, t0);
        store.Record(new Aircraft { Hex = "a", Lat = 34.0, Lon = -84 }, t0.AddSeconds(10)); // repeat: skipped, LastSeen=t10
        store.Record(new Aircraft { Hex = "a", Lat = 33.9, Lon = -84 }, t0.AddSeconds(5));  // late, older sample
        var trail = store.Get("a")!;
        Assert.Equal([34.0], trail.Points.Select(p => p.Lat));
        Assert.Equal(t0.AddSeconds(10).ToUnixTimeSeconds(), trail.LastSeen, 3);
    }

    private sealed class SucceedsOnceThenFails : IFlightDataProvider
    {
        private int _n;
        public string Name => "once";
        public Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double r, CancellationToken ct) =>
            Interlocked.Increment(ref _n) == 1
                ? Task.FromResult(new ProviderResult([new Aircraft { Hex = "ok1", Lat = c.Lat, Lon = c.Lon, AltitudeFt = 5000 }], Name))
                : throw new HttpRequestException("down");
        public Task<ProviderResult> GetByCallsignAsync(string cs, CancellationToken ct) => Task.FromResult(new ProviderResult([], Name));
    }

    [Fact]
    public async Task FailureAfterSuccess_KeepsLastGoodTimestampAndAircraft()
    {
        using var db = new TestDb();
        var (reg, _, _) = Build(db, new SucceedsOnceThenFails(), new TrailStore(), pollSeconds: 2); // poll 1 succeeds, later polls fail
        using var lease = reg.Acquire(new WallSettings { CenterLat = 34, CenterLon = -84 });
        await WaitFor(() => lease.Store.Current.UpdatedAt != DateTimeOffset.MinValue);
        var good = lease.Store.Current;
        Assert.Null(good.Error);

        // The next poll (2 s minimum interval) fails.
        for (var i = 0; i < 100 && lease.Store.Current.Error is null; i++) await Task.Delay(50);
        var after = lease.Store.Current;
        Assert.NotNull(after.Error);
        Assert.Equal(good.UpdatedAt, after.UpdatedAt);
        Assert.Equal(["ok1"], after.MapAircraft.Select(a => a.Hex));
    }
}
