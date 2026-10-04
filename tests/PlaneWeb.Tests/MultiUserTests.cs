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

    private static (PollerRegistry Reg, ManualTime Time, TraceBackfillService Backfill) Build(TestDb db, IFlightDataProvider provider)
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var opt = Options.Create(new PlaneWebOptions { PollSeconds = 60 });
        var trails = new TrailStore();
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
}
