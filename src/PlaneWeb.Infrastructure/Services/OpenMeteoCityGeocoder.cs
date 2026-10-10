using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PlaneWeb.Core;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>
/// Bounded cache of city search results. Shared (singleton) because typed HTTP clients are created per injection.
/// Entry count is capped so user-typed queries cannot grow memory without limit.
/// </summary>
public sealed class CityGeocoderCache(int maxEntries = 500)
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = maxEntries });

    public bool TryGet(string key, out IReadOnlyList<CityResult> results)
    {
        if (_cache.TryGetValue(key, out IReadOnlyList<CityResult>? hit) && hit is not null) { results = hit; return true; }
        results = [];
        return false;
    }

    public void Set(string key, IReadOnlyList<CityResult> results, TimeSpan ttl) =>
        _cache.Set(key, results, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
}

/// <summary>City search via the Open-Meteo geocoding API (free, no key). Failures yield an empty list.</summary>
public sealed class OpenMeteoCityGeocoder(
    HttpClient client,
    CityGeocoderCache cache,
    ILogger<OpenMeteoCityGeocoder> logger) : ICityGeocoder
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);
    /// <summary>No real city name is longer; rejecting longer input bounds cache keys and upstream URLs.</summary>
    public const int MaxQueryLength = 80;

    public async Task<IReadOnlyList<CityResult>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2 || query.Length > MaxQueryLength) return [];

        var key = "city:" + query.ToLowerInvariant();
        if (cache.TryGet(key, out var cached)) return cached;

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

            cache.Set(key, list, CacheFor);
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
