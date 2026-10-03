using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace FlightWall.Infrastructure.Providers;

/// <summary>
/// Fetches airline logos by ICAO code from a public logo set and caches them on disk,
/// so wall displays never hotlink and logos survive restarts.
/// </summary>
public sealed partial class AirlineLogoService(HttpClient http, string cacheDir, ILogger<AirlineLogoService> log)
{
    private static readonly TimeSpan MissTtl = TimeSpan.FromHours(12);
    /// <summary>Short TTL for transient failures (5xx/timeouts): coalesces queued requests during an outage,
    /// and stays below the client's first retry (30s) so recovery is picked up promptly.</summary>
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _misses = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex IcaoCode();

    /// <summary>Returns the path to a cached PNG, or null if no logo exists.</summary>
    public async Task<string?> GetLogoPathAsync(string icao, CancellationToken ct)
    {
        icao = icao.ToUpperInvariant();
        if (!IcaoCode().IsMatch(icao)) return null;
        var path = Path.Combine(cacheDir, icao + ".png");
        if (File.Exists(path)) return path;
        if (_misses.TryGetValue(icao, out var until) && until > DateTimeOffset.UtcNow) return null;

        var gate = _locks.GetOrAdd(icao, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check both caches: requests queued behind the first one must not refetch.
            if (File.Exists(path)) return path;
            if (_misses.TryGetValue(icao, out until) && until > DateTimeOffset.UtcNow) return null;
            using var resp = await http.GetAsync($"{icao}.png", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                _misses[icao] = DateTimeOffset.UtcNow + MissTtl;
                return null;
            }
            resp.EnsureSuccessStatusCode();
            Directory.CreateDirectory(cacheDir);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, await resp.Content.ReadAsByteArrayAsync(ct), ct);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Logo fetch failed for {Icao}", icao);
            _misses[icao] = DateTimeOffset.UtcNow + FailureTtl;
            return null;
        }
        finally { gate.Release(); }
    }
}
