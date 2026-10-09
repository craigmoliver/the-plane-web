using PlaneWeb.Core;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>Uses https://geocoding-api.open-meteo.com/v1/search to find cities.</summary>
public sealed class OpenMeteoCityGeocoder(
    HttpClient client,
    IMemoryCache cache,
    ILogger<OpenMeteoCityGeocoder> logger) : ICityGeocoder
{
    private static readonly Uri BaseUri = new("https://geocoding-api.open-meteo.com/v1/search");

    public async Task<IReadOnlyList<CityResult>> SearchAsync(string query, CancellationToken ct)
    {
        // Trim and check minimum length.
        query = query.Trim();
        if (query.Length < 2)
        {
            logger.LogDebug("Query '{Query}' is too short to search", query);
            return [];
        }

        // Check for cached result.
        var key = query.ToLowerInvariant();
        if (cache.TryGetValue<IReadOnlyList<CityResult>>(key, out var cached))
        {
            logger.LogDebug("Returning cached results for '{Query}'", query);
            return cached;
        }

        // Build the request URI.
        var uri = new Uri(BaseUri, $"?name={Uri.EscapeDataString(query)}&count=8&language=en&format=json");

        try
        {
            using var response = await client.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("HTTP {Status} when searching for '{Query}'", response.StatusCode, query);
                return [];
            }

            // Read the JSON body.
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (json.RootElement.TryGetProperty("results", out var results))
            {
                var list = new List<CityResult>(results.GetArrayLength());
                foreach (var item in results.EnumerateArray())
                {
                    list.Add(new(
                        Name: item.GetProperty("name").GetString() ?? "",
                        Region: item.GetProperty("admin1").GetString(),
                        Country: item.GetProperty("country").GetString(),
                        Lat: item.GetProperty("latitude").GetDouble(),
                        Lon: item.GetProperty("longitude").GetDouble()
                    ));
                }

                // Cache the results for 1 day.
                cache.Set(key, list, TimeSpan.FromDays(1));
                logger.LogDebug("Found {Count} results for '{Query}'", list.Count, query);
                return list;
            }
            else
            {
                logger.LogDebug("No results found for '{Query}'", query);
                return [];
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error searching for city '{Query}'", query);
            return [];
        }
    }
}