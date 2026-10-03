using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using PlaneWeb.Core;
using Microsoft.Extensions.Logging;

namespace PlaneWeb.Infrastructure.Providers;

/// <summary>
/// Route lookup using the adsb.lol VRS standing data (static JSON per callsign).
/// e.g. https://vrs-standing-data.adsb.lol/routes/AS/ASA1.json
/// </summary>
public sealed class VrsRouteLookup(HttpClient http, ILogger<VrsRouteLookup> log) : IRouteLookup
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, (FlightRoute? Route, DateTimeOffset Expires)> _cache = new();

    public async Task<FlightRoute?> GetRouteAsync(string callsign, CancellationToken ct)
    {
        callsign = callsign.Trim().ToUpperInvariant();
        if (callsign.Length < 3) return null;
        if (_cache.TryGetValue(callsign, out var c) && c.Expires > DateTimeOffset.UtcNow) return c.Route;

        FlightRoute? route = null;
        try
        {
            using var resp = await http.GetAsync($"routes/{callsign[..2]}/{Uri.EscapeDataString(callsign)}.json", ct);
            if (resp.IsSuccessStatusCode)
                route = Parse(await resp.Content.ReadAsStringAsync(ct));
            else if (resp.StatusCode != HttpStatusCode.NotFound)
                return c.Route; // transient: don't cache
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Route lookup failed for {Callsign}", callsign);
            return c.Route;
        }

        _cache[callsign] = (route, DateTimeOffset.UtcNow + (route is null ? MissTtl : HitTtl));
        return route;
    }

    public static FlightRoute? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("_airports", out var aps) || aps.ValueKind != JsonValueKind.Array) return null;
        var airports = aps.EnumerateArray().Select(a => new Airport(
            S(a, "icao") ?? "", S(a, "iata"), S(a, "name") ?? "", S(a, "location"),
            a.TryGetProperty("lat", out var la) ? la.GetDouble() : 0,
            a.TryGetProperty("lon", out var lo) ? lo.GetDouble() : 0)).ToList();
        if (airports.Count < 2) return null;
        // Multi-leg routes keep all stops; the active leg is resolved against the live position.
        return new FlightRoute(S(root, "callsign") ?? "", airports[0], airports[^1]) { Stops = airports };
    }

    private static string? S(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
