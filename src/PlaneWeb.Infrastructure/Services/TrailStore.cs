using System.Text.Json;
using PlaneWeb.Core;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>A read-only copy of one aircraft's trail. <see cref="Revision"/> changes when older points are merged in.</summary>
public sealed record TrailSnapshot(string Hex, int Revision, double LastSeen, IReadOnlyList<TrailPoint> Points);

/// <summary>
/// In-memory flight paths keyed by ICAO hex, built from live polls plus backfilled history.
/// Thread-safe; trimmed to <see cref="Window"/> and dropped when an aircraft is not seen for <see cref="StaleAfter"/>.
/// </summary>
public sealed class TrailStore
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    /// <summary>Upper bound per aircraft so a bad feed can't grow memory without limit.</summary>
    public const int MaxPointsPerTrail = 2000;

    private sealed class Trail
    {
        public List<TrailPoint> Points { get; } = [];
        public int Revision;
        public double LastSeen;
    }

    private readonly Dictionary<string, Trail> _trails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private TimeSpan _window = TimeSpan.FromMinutes(15);

    public TimeSpan Window
    {
        get { lock (_lock) return _window; }
        set { lock (_lock) _window = value <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : value; }
    }

    public int Count { get { lock (_lock) return _trails.Count; } }

    /// <summary>Appends the aircraft's current position. Returns true if this hex was not known before.</summary>
    public bool Record(Aircraft a, DateTimeOffset now)
    {
        if (a.Position is not { } p) return false;
        var t = now.ToUnixTimeMilliseconds() / 1000.0;
        var point = new TrailPoint(t, p.Lat, p.Lon, a.OnGround ? 0 : a.AltitudeFt);
        lock (_lock)
        {
            var isNew = !_trails.TryGetValue(a.Hex, out var trail);
            if (trail is null) _trails[a.Hex] = trail = new Trail();
            // A late-arriving sample older than the newest sighting is stale: it must neither rewind freshness
            // nor be appended (the last stored point can be older than LastSeen when repeats were skipped).
            if (!isNew && t < trail.LastSeen) return false;
            trail.LastSeen = t;
            var pts = trail.Points;
            // Skip repeats: the feed returns the same position until the aircraft reports a new one.
            if (pts.Count == 0 || (pts[^1].T < t && (pts[^1].Lat != point.Lat || pts[^1].Lon != point.Lon)))
            {
                pts.Add(point);
                if (pts.Count > MaxPointsPerTrail) pts.RemoveRange(0, pts.Count - MaxPointsPerTrail);
            }
            return isNew;
        }
    }

    /// <summary>Merges older history (e.g. from a trace file) into a trail, keeping points ordered and de-duplicated.</summary>
    public void Merge(string hex, IEnumerable<TrailPoint> history, DateTimeOffset now)
    {
        var cutoff = now.ToUnixTimeMilliseconds() / 1000.0 - Window.TotalSeconds;
        var incoming = history.Where(p => p.T >= cutoff).ToList();
        if (incoming.Count == 0) return;
        lock (_lock)
        {
            if (!_trails.TryGetValue(hex, out var trail)) return; // aircraft already gone
            var merged = trail.Points.Concat(incoming).OrderBy(p => p.T).ToList();
            trail.Points.Clear();
            foreach (var p in merged)
            {
                // Treat points within a second of each other as the same report.
                if (trail.Points.Count > 0 && p.T - trail.Points[^1].T < 1) continue;
                trail.Points.Add(p);
            }
            if (trail.Points.Count > MaxPointsPerTrail)
                trail.Points.RemoveRange(0, trail.Points.Count - MaxPointsPerTrail);
            trail.Revision++;
        }
    }

    /// <summary>Drops points older than the window and aircraft not seen recently.</summary>
    public void Prune(DateTimeOffset now)
    {
        var nowSec = now.ToUnixTimeMilliseconds() / 1000.0;
        lock (_lock)
        {
            var cutoff = nowSec - _window.TotalSeconds;
            foreach (var (hex, trail) in _trails.ToList())
            {
                if (nowSec - trail.LastSeen > StaleAfter.TotalSeconds) { _trails.Remove(hex); continue; }
                var drop = trail.Points.FindIndex(p => p.T >= cutoff);
                if (drop < 0) trail.Points.Clear();
                else if (drop > 0) trail.Points.RemoveRange(0, drop);
            }
        }
    }

    public IReadOnlyList<TrailSnapshot> Snapshot()
    {
        lock (_lock)
            return _trails.Select(kv => new TrailSnapshot(kv.Key, kv.Value.Revision, kv.Value.LastSeen, kv.Value.Points.ToArray())).ToList();
    }

    public TrailSnapshot? Get(string hex)
    {
        lock (_lock)
            return _trails.TryGetValue(hex, out var t) ? new TrailSnapshot(hex, t.Revision, t.LastSeen, t.Points.ToArray()) : null;
    }

    // ---- persistence ----

    private sealed record FileModel(int Version, List<FileTrail> Trails);
    private sealed record FileTrail(string Hex, double LastSeen, List<double?[]> Points);

    public string Serialize()
    {
        var model = new FileModel(1, Snapshot().Select(s => new FileTrail(s.Hex, s.LastSeen,
            s.Points.Select(p => new double?[] { p.T, p.Lat, p.Lon, p.AltFt }).ToList())).ToList());
        return JsonSerializer.Serialize(model);
    }

    /// <summary>Replaces contents from a saved file, then prunes anything stale. Returns the number of trails kept.</summary>
    public int Load(string json, DateTimeOffset now)
    {
        var model = JsonSerializer.Deserialize<FileModel>(json);
        lock (_lock)
        {
            _trails.Clear();
            foreach (var ft in model?.Trails ?? [])
            {
                if (string.IsNullOrEmpty(ft.Hex)) continue;
                var trail = new Trail { LastSeen = ft.LastSeen, Revision = 1 };
                foreach (var a in ft.Points)
                    if (a is [{ } t, { } lat, { } lon, var alt, ..])
                        trail.Points.Add(new TrailPoint(t, lat, lon, alt is { } v ? (int)v : null));
                trail.Points.Sort((x, y) => x.T.CompareTo(y.T));
                _trails[ft.Hex] = trail;
            }
        }
        Prune(now);
        return Count;
    }

    /// <summary>Writes atomically (temp file + rename) so a crash mid-write can't corrupt the file.</summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Unique temp name: during an Azure rollout the old and new replica briefly share /data.
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, Serialize(), ct);
        File.Move(tmp, path, overwrite: true);
    }
}
