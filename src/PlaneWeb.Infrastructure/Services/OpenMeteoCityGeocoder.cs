using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PlaneWeb.Core;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>City search via the Open-Meteo geocoding API (free, no key). Failures yield an empty list.</summary>
public sealed class OpenMeteoCityGeocoder(
    HttpClient client,
    IMemoryCache cache,
    ILogger<OpenMeteoCityGeocoder> logger) : ICityGeocoder
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);

    public async Task<IReadOnlyList<CityResult>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return [];

        var key = "city:" + query.ToLowerInvariant();
        if (cache.TryGetValue(key, out IReadOnlyList<CityResult>? cached) && cached is not null) return cached;

        try
        {
            using var response = await client.GetAsync(
                $"v1/search?name={Uri.EscapeDataString(query)}&count=8&language=en&format=json", ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("City search for '{Query}' returned HTTP {Status}", query, (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var list = new List<CityResult>();
            if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var name = Str(item, "name");
                    if (name is null || !item.TryGetProperty("latitude", out var lat) || !item.TryGetProperty("longitude", out var lon)
                        || lat.ValueKind != JsonValueKind.Number || lon.ValueKind != JsonValueKind.Number) continue;
                    list.Add(new CityResult(name, Str(item, "admin1"), Str(item, "country"), lat.GetDouble(), lon.GetDouble()));
                }
            }

            cache.Set(key, (IReadOnlyList<CityResult>)list, CacheFor);
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "City search for '{Query}' failed", query);
            return [];
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
