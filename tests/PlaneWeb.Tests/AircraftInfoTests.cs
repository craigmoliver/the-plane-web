using PlaneWeb.Infrastructure.Providers;

namespace PlaneWeb.Tests;

public class AircraftInfoTests
{
    private const string Adsbdb = """
    {"response":{"aircraft":{"type":"717 231","icao_type":"B712","manufacturer":"Boeing","mode_s":"ACF84E","registration":"N935AT",
     "registered_owner_country_name":"United States","registered_owner_operator_flag_code":"DAL","registered_owner":"Delta Air Lines",
     "url_photo":"https://image.airport-data.com/aircraft/001616628.jpg"}}}
    """;
    private const string Spotters = """
    {"photos":[{"id":"1910899","thumbnail":{"src":"https://t.plnspttrs.net/05800/1910899_4afb4a101b_t.jpg","size":{"width":200,"height":133}},
     "thumbnail_large":{"src":"https://t.plnspttrs.net/05800/1910899_4afb4a101b_280.jpg","size":{"width":420,"height":280}},
     "link":"https://www.planespotters.net/photo/1910899/n935at-delta-air-lines-boeing-717-231?utm_source=api","photographer":"Alessandro Brown"}]}
    """;

    [Fact]
    public void ParsesAdsbdbAircraft()
    {
        var a = AircraftInfoService.ParseAdsbdb("acf84e", Adsbdb)!;
        Assert.Equal("N935AT", a.Registration);
        Assert.Equal("Boeing", a.Manufacturer);
        Assert.Equal("B712", a.IcaoType);
        Assert.Equal("Delta Air Lines", a.Owner);
        Assert.Null(AircraftInfoService.ParseAdsbdb("x", """{"response":"unknown aircraft"}"""));
    }

    [Fact]
    public void ParsesPlanespottersPhoto_AndRejectsNonHttps()
    {
        var p = AircraftInfoService.ParsePlanespotters(Spotters)!;
        Assert.EndsWith("_280.jpg", p.Url);
        Assert.Equal(420, p.Width);
        Assert.Equal("Alessandro Brown", p.Photographer);
        Assert.StartsWith("https://www.planespotters.net/photo/", p.Link);
        Assert.Null(AircraftInfoService.ParsePlanespotters("""{"photos":[]}"""));
        Assert.Null(AircraftInfoService.ParsePlanespotters("""{"photos":[{"thumbnail_large":{"src":"javascript:alert(1)"}}]}"""));
    }

    private sealed class Handler(string body, int delayMs = 0) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> Paths = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Paths) Paths.Add(r.RequestUri!.AbsolutePath);
            await Task.Delay(delayMs, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task LooksUpPhotoByRegistration_CachesAndCoalesces()
    {
        var db = new Handler(Adsbdb, 30);
        var ps = new Handler(Spotters);
        var svc = new AircraftInfoService(
            new HttpClient(db) { BaseAddress = new Uri("https://db/") },
            new HttpClient(ps) { BaseAddress = new Uri("https://ps/") },
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<AircraftInfoService>.Instance);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => svc.GetAsync("ACF84E", null, default)));
        Assert.All(results, r => Assert.Equal("N935AT", r!.Registration));
        Assert.NotNull(results[0]!.Photo);
        await svc.GetAsync("acf84e", null, default);
        Assert.Equal(1, db.Calls);
        Assert.Equal(["/pub/photos/reg/N935AT"], ps.Paths);
        Assert.Null(await svc.GetAsync("../etc", null, default));
    }

    private sealed class Failing : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task FailedLookupWithRegistrationHint_UsesShortCacheLifetime()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-03T12:00:00Z"));
        var db = new Failing();
        var svc = new AircraftInfoService(
            new HttpClient(db) { BaseAddress = new Uri("https://db/") },
            new HttpClient(new Failing()) { BaseAddress = new Uri("https://ps/") },
            time, Microsoft.Extensions.Logging.Abstractions.NullLogger<AircraftInfoService>.Instance);

        var r = await svc.GetAsync("abc123", "N1", default);
        Assert.Equal("N1", r!.Registration);
        Assert.False(r.Found);
        time.Now += TimeSpan.FromMinutes(31);
        await svc.GetAsync("abc123", "N1", default);
        Assert.Equal(2, db.Calls); // retried after the 30-minute failure window, not 12 hours
    }

    [Fact]
    public async Task RegistrationHint_IsPartOfCacheKey()
    {
        var db = new Failing();
        var ps = new Handler(Spotters);
        var svc = new AircraftInfoService(
            new HttpClient(db) { BaseAddress = new Uri("https://db/") },
            new HttpClient(ps) { BaseAddress = new Uri("https://ps/") },
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<AircraftInfoService>.Instance);
        Assert.Null((await svc.GetAsync("abc123", null, default))!.Registration);
        Assert.Equal("N1", (await svc.GetAsync("abc123", "n1", default))!.Registration);
        Assert.Contains("/pub/photos/reg/N1", ps.Paths);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
