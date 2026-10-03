using System.Text.Json;
using PlaneWeb.Core;

namespace PlaneWeb.Infrastructure.Providers;

/// <summary>
/// Reads an aircraft's recent path from adsb.lol's tar1090 trace files
/// (<c>/data/traces/{last2}/trace_recent_{hex}.json</c>). This endpoint is not an official API,
/// so callers must treat failure as normal.
/// </summary>
public sealed class AdsbLolTraceClient(HttpClient http)
{
    public async Task<IReadOnlyList<TrailPoint>> GetRecentAsync(string hex, CancellationToken ct)
    {
        hex = hex.Trim().ToLowerInvariant();
        if (hex.Length < 2 || !hex.All(Uri.IsHexDigit)) return [];
        return TraceParser.Parse(await http.GetStringAsync($"data/traces/{hex[^2..]}/trace_recent_{hex}.json", ct));
    }
}

public static class TraceParser
{
    /// <summary>
    /// Parses <c>{"timestamp": base, "trace": [[offsetSec, lat, lon, alt|"ground"|null, gs, track, ...], ...]}</c>.
    /// </summary>
    public static IReadOnlyList<TrailPoint> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Number) return [];
        if (!root.TryGetProperty("trace", out var trace) || trace.ValueKind != JsonValueKind.Array) return [];
        var baseT = ts.GetDouble();

        var list = new List<TrailPoint>(trace.GetArrayLength());
        foreach (var row in trace.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4) continue;
            if (row[0].ValueKind != JsonValueKind.Number || row[1].ValueKind != JsonValueKind.Number ||
                row[2].ValueKind != JsonValueKind.Number) continue;
            int? alt = row[3].ValueKind switch
            {
                JsonValueKind.Number => (int)row[3].GetDouble(),
                JsonValueKind.String when row[3].GetString() == "ground" => 0,
                _ => null,
            };
            list.Add(new TrailPoint(baseT + row[0].GetDouble(), row[1].GetDouble(), row[2].GetDouble(), alt));
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }
}
