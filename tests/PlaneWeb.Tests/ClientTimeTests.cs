using Microsoft.JSInterop;
using PlaneWeb.Web;

namespace PlaneWeb.Tests;

public class ClientTimeTests
{
    private sealed class FakeJs(Func<string?> zone) : IJSRuntime
    {
        public int Calls;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls++;
            Assert.Equal("planeWebTimeZone", identifier);
            return ValueTask.FromResult((TValue)(object?)zone()!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private static readonly DateTimeOffset Summer = DateTimeOffset.Parse("2026-07-01T12:00:00Z");

    [Fact]
    public async Task UsesTheBrowsersZone_AskingOnlyOnce()
    {
        var js = new FakeJs(() => "Asia/Tokyo");
        var clock = new ClientTime(js);
        Assert.False(clock.IsResolved);
        await clock.EnsureAsync();
        await clock.EnsureAsync();
        Assert.True(clock.IsResolved);
        Assert.Equal(1, js.Calls);
        Assert.Equal("21:00", clock.Format(Summer, "HH:mm")); // UTC+9
    }

    [Fact]
    public async Task FollowsDaylightSavingForTheGivenDate()
    {
        var clock = new ClientTime(new FakeJs(() => "America/New_York"));
        await clock.EnsureAsync();
        Assert.Equal("08:00", clock.Format(Summer, "HH:mm"));                                     // EDT, UTC-4
        Assert.Equal("07:00", clock.Format(DateTimeOffset.Parse("2026-01-15T12:00:00Z"), "HH:mm")); // EST, UTC-5
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    [InlineData(null)]
    public async Task UnknownOrMissingZone_KeepsTheServersZone(string? id)
    {
        var clock = new ClientTime(new FakeJs(() => id));
        await clock.EnsureAsync();
        Assert.True(clock.IsResolved);
        Assert.Equal(TimeZoneInfo.Local, clock.Zone);
    }

    [Fact]
    public async Task InteropFailure_KeepsTheServersZone()
    {
        var clock = new ClientTime(new FakeJs(() => throw new JSException("no")));
        await clock.EnsureAsync();
        Assert.True(clock.IsResolved);
        Assert.Equal(TimeZoneInfo.Local, clock.Zone);
    }
}
