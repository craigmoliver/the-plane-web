using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PlaneWeb.Infrastructure.Services;

namespace PlaneWeb.Tests;

public class CityGeocoderTests
{
    private const string Body = """
    {"results":[
      {"name":"Woodstock","admin1":"Georgia","country":"United States","latitude":34.10149,"longitude":-84.51937},
      {"name":"Nowhereville","latitude":1.5,"longitude":2.5},
      {"name":"Broken","admin1":"X"}
    ]}
    """;

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
    {
        public int Calls;
        public Uri? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastUri = r.RequestUri;
            return fn(r, ct);
        }
    }

    private static Handler Ok(string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

    private static OpenMeteoCityGeocoder Make(Handler h, string baseUrl = "https://geo.test/") =>
        new(new HttpClient(h) { BaseAddress = new Uri(baseUrl) }, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OpenMeteoCityGeocoder>.Instance);

    [Fact]
    public async Task ParsesResults_ToleratesMissingFields_AndHonoursBaseAddress()
    {
        var h = Ok(Body);
        var r = await Make(h, "https://custom.test/").SearchAsync("  Wood stock ", default);

        Assert.Equal(2, r.Count);
        Assert.Equal("Woodstock, Georgia", r[0].Label);
        Assert.Equal(34.10149, r[0].Lat);
        Assert.Null(r[1].Region);
        Assert.Equal("custom.test", h.LastUri!.Host);
        Assert.Equal("/v1/search", h.LastUri.AbsolutePath);
        Assert.Contains("name=Wood%20stock", h.LastUri.Query);
    }

    [Fact]
    public async Task MissingResults_ShortQuery_AndHttpErrors_ReturnEmpty()
    {
        var empty = Ok("""{"generationtime_ms":0.1}""");
        Assert.Empty(await Make(empty).SearchAsync("zzzz", default));

        var none = Ok(Body);
        Assert.Empty(await Make(none).SearchAsync("a", default));
        Assert.Equal(0, none.Calls);

        var err = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Empty(await Make(err).SearchAsync("atlanta", default));

        var boom = new Handler((_, _) => throw new HttpRequestException("down"));
        Assert.Empty(await Make(boom).SearchAsync("atlanta", default));
    }

    [Fact]
    public async Task CachesResults_CaseInsensitively()
    {
        var h = Ok(Body);
        var svc = Make(h);
        await svc.SearchAsync("Woodstock", default);
        await svc.SearchAsync("woodstock", default);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Cancellation_IsPropagated()
    {
        var h = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(h).SearchAsync("atlanta", cts.Token));
    }
}
