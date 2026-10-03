using System.Globalization;
using System.Text.Json;
using PlaneWeb.Core;

namespace PlaneWeb.Infrastructure.Providers;

/// <summary>Parses readsb/tar1090-style JSON (used by adsb.lol, adsb.fi, airplanes.live).</summary>
public static class ReadsbParser
{
    public static IReadOnlyList<Aircraft> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ac", out var arr) && !root.TryGetProperty("aircraft", out arr))
            return [];
        if (arr.ValueKind != JsonValueKind.Array) return [];

        var list = new List<Aircraft>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            var hex = Str(e, "hex");
            if (string.IsNullOrEmpty(hex)) continue;

            int? alt = null;
            var onGround = false;
            if (e.TryGetProperty("alt_baro", out var ab))
            {
                if (ab.ValueKind == JsonValueKind.Number) alt = (int)ab.GetDouble();
                else if (ab.ValueKind == JsonValueKind.String && ab.GetString() == "ground") { alt = 0; onGround = true; }
            }
            alt ??= (int?)Num(e, "alt_geom");

            list.Add(new Aircraft
            {
                Hex = hex.TrimStart('~'),
                Callsign = Str(e, "flight")?.Trim() is { Length: > 0 } f ? f : null,
                Registration = Str(e, "r"),
                TypeCode = Str(e, "t"),
                Description = Str(e, "desc"),
                Operator = Str(e, "ownOp"),
                Category = Str(e, "category"),
                Lat = Num(e, "lat") ?? (e.TryGetProperty("lastPosition", out var lp) ? Num(lp, "lat") : null),
                Lon = Num(e, "lon") ?? (e.TryGetProperty("lastPosition", out var lp2) ? Num(lp2, "lon") : null),
                AltitudeFt = alt,
                OnGround = onGround,
                GroundSpeedKt = Num(e, "gs"),
                TrackDeg = Num(e, "track") ?? Num(e, "true_heading"),
                VerticalRateFpm = Num(e, "baro_rate") ?? Num(e, "geom_rate"),
                Squawk = Str(e, "squawk"),
            });
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    internal static string F(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);
}

public sealed class AdsbLolProvider(HttpClient http) : IFlightDataProvider
{
    public string Name => "adsb.lol";

    public async Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double radiusNm, CancellationToken ct)
    {
        var r = (int)Math.Clamp(Math.Ceiling(radiusNm), 1, 250);
        return new(ReadsbParser.Parse(await http.GetStringAsync(
            $"v2/point/{ReadsbParser.F(c.Lat)}/{ReadsbParser.F(c.Lon)}/{r}", ct)), Name);
    }

    public async Task<ProviderResult> GetByCallsignAsync(string callsign, CancellationToken ct) =>
        new(ReadsbParser.Parse(await http.GetStringAsync($"v2/callsign/{Uri.EscapeDataString(callsign)}", ct)), Name);
}

public sealed class AdsbFiProvider(HttpClient http) : IFlightDataProvider
{
    public string Name => "adsb.fi";

    public async Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double radiusNm, CancellationToken ct)
    {
        var r = (int)Math.Clamp(Math.Ceiling(radiusNm), 1, 250);
        return new(ReadsbParser.Parse(await http.GetStringAsync(
            $"api/v2/lat/{ReadsbParser.F(c.Lat)}/lon/{ReadsbParser.F(c.Lon)}/dist/{r}", ct)), Name);
    }

    public async Task<ProviderResult> GetByCallsignAsync(string callsign, CancellationToken ct) =>
        new(ReadsbParser.Parse(await http.GetStringAsync($"api/v2/callsign/{Uri.EscapeDataString(callsign)}", ct)), Name);
}

/// <summary>Tries each provider in order until one succeeds.</summary>
public sealed class FallbackFlightDataProvider(IEnumerable<IFlightDataProvider> providers,
    Microsoft.Extensions.Logging.ILogger<FallbackFlightDataProvider> log) : IFlightDataProvider
{
    private readonly IFlightDataProvider[] _providers = providers.ToArray();
    public string Name => string.Join("/", _providers.Select(p => p.Name));

    public Task<ProviderResult> GetAircraftNearAsync(GeoPoint c, double r, CancellationToken ct) =>
        Try(p => p.GetAircraftNearAsync(c, r, ct));

    public Task<ProviderResult> GetByCallsignAsync(string cs, CancellationToken ct) =>
        Try(p => p.GetByCallsignAsync(cs, ct));

    private async Task<ProviderResult> Try(Func<IFlightDataProvider, Task<ProviderResult>> f)
    {
        Exception? last = null;
        foreach (var p in _providers)
        {
            try
            {
                return await f(p);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(log, ex, "Provider {Provider} failed", p.Name);
            }
        }
        throw new InvalidOperationException("All flight data providers failed", last);
    }
}
