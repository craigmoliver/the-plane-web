using PlaneWeb.Core;
using System.Threading.Channels;
using PlaneWeb.Infrastructure.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>
/// Fetches the earlier path of newly seen aircraft, one request at a time with a minimum gap,
/// and merges it into the <see cref="TrailStore"/>. Each hex is attempted at most once per hour.
/// </summary>
public sealed class TraceBackfillService(
    AdsbLolTraceClient client,
    TrailStore trails,
    IOptions<PlaneWebOptions> options,
    TimeProvider time,
    ILogger<TraceBackfillService> log) : BackgroundService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(60);
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Dictionary<string, DateTimeOffset> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>Mirrors the TraceBackfill setting; when false, queued lookups are skipped instead of requested.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Queues a lookup unless this hex was tried recently. Returns true if queued.</summary>
    public bool Enqueue(string hex)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_attempted.TryGetValue(hex, out var at) && now - at < RetryAfter) return false;
            // Only count work the queue accepted; if it is full, a later poll will try again.
            if (!_queue.Writer.TryWrite(hex)) return false;
            _attempted[hex] = now;
            if (_attempted.Count > 5000)
                foreach (var k in _attempted.Where(kv => now - kv.Value >= RetryAfter).Select(kv => kv.Key).ToList())
                    _attempted.Remove(k);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gap = TimeSpan.FromMilliseconds(Math.Max(250, options.Value.TraceMinIntervalMs));
        await foreach (var hex in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!Enabled)
            {
                // Forget the attempt so the aircraft is looked up if history is turned back on.
                lock (_lock) _attempted.Remove(hex);
                continue;
            }
            try
            {
                if (trails.Get(hex) is not null) // skip if the aircraft already left
                {
                    var points = await client.GetRecentAsync(hex, stoppingToken);
                    trails.Merge(hex, points, time.GetUtcNow());
                    log.LogDebug("Backfilled {Count} points for {Hex}", points.Count, hex);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Shared rate limit with the live feed: back off so polling isn't starved, and retry this one later.
                log.LogInformation("adsb.lol rate-limited history lookups; pausing {Seconds}s", RateLimitPause.TotalSeconds);
                lock (_lock) _attempted.Remove(hex);
                await Task.Delay(RateLimitPause, time, stoppingToken);
                if (Enabled) Enqueue(hex);
                continue;
            }
            catch (Exception ex)
            {
                // Unofficial endpoint: 404s are normal for some aircraft; keep logs quiet.
                log.LogDebug(ex, "Trace backfill failed for {Hex}", hex);
            }
            await Task.Delay(gap, time, stoppingToken);
        }
    }
}

/// <summary>Loads trails at startup, saves them every minute and on shutdown.</summary>
public sealed class TrailPersistenceService(
    TrailStore trails,
    SettingsService settings,
    IOptions<PlaneWebOptions> options,
    TimeProvider time,
    ILogger<TrailPersistenceService> log) : BackgroundService
{
    private string Path => options.Value.TrailsFile;

    public override async Task StartAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(Path))
            {
                // Apply the saved path length first so restoring doesn't trim to the default window.
                var s = await settings.GetAsync(ct);
                trails.Window = TimeSpan.FromMinutes(Math.Clamp(s.TrailMinutes, 1, WallSettings.MaxTrailMinutes));
                var kept = trails.Load(await File.ReadAllTextAsync(Path, ct), time.GetUtcNow());
                log.LogInformation("Restored {Count} flight trails from {Path}", kept, Path);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not read saved trails from {Path}; starting empty", Path); }
        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await SaveAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await SaveAsync(CancellationToken.None); // always attempt the final save
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await trails.SaveAsync(Path, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not save trails to {Path}", Path); }
    }
}
