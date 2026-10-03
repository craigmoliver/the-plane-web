using System.Collections.Concurrent;
using System.Text.Json;
using PlaneWeb.Core;
using Microsoft.Extensions.Logging;

namespace PlaneWeb.Infrastructure.Providers;

public sealed record AircraftPhoto(string Url, int? Width, int? Height, string? Link, string? Photographer, string Source);

/// <summary>Registry details for an airframe (adsbdb.com) plus a photo (planespotters.net).</summary>
public sealed record AircraftInfo
{
    public required string Hex { get; init; }
    public string? Registration { get; init; }
    public string? Manufacturer { get; init; }
    public string? Type { get; init; }
    public string? IcaoType { get; init; }
    public string? Owner { get; init; }
    public string? OwnerCountry { get; init; }
    public string? OperatorFlag { get; init; }
    public AircraftPhoto? Photo { get; init; }
    /// <summary>False when neither upstream returned data (only the caller's hints are present).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Found { get; init; }
}

/// <summary>
/// Looks up airframe details and a photo, cached in memory (hits 12 h, misses/failures 30 min)
/// with concurrent requests for the same hex coalesced into one upstream call.
/// </summary>
public sealed class AircraftInfoService(HttpClient adsbdb, HttpClient planespotters, TimeProvider time, ILogger<AircraftInfoService> log)
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(12), MissTtl = TimeSpan.FromMinutes(30);
    private const int MaxEntries = 2000;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, Lazy<Task<AircraftInfo>> Value)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<AircraftInfo?> GetAsync(string hex, string? registration, CancellationToken ct)
    {
        hex = hex.Trim().ToLowerInvariant();
        if (hex.Length is < 2 or > 8 || !hex.All(Uri.IsHexDigit)) return null;
        registration = string.IsNullOrWhiteSpace(registration) ? null : registration.Trim().ToUpperInvariant();
        // The registration hint changes the result (fallback registration, photo lookup), so it is part of the key.
        var key = $"{hex}|{registration}";
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var e) && e.Expires > now) return await e.Value.Value.WaitAsync(ct);

        if (_cache.Count >= MaxEntries)
        {
            foreach (var k in _cache.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) _cache.TryRemove(k, out _);
            // Hard cap: drop the entries closest to expiry until there is room.
            if (_cache.Count >= MaxEntries)
                foreach (var k in _cache.OrderBy(kv => kv.Value.Expires).Take(_cache.Count - MaxEntries / 2).Select(kv => kv.Key).ToList())
                    _cache.TryRemove(k, out _);
        }

        Lazy<Task<AircraftInfo>>? lazy = null;
        lazy = new Lazy<Task<AircraftInfo>>(async () =>
        {
            var info = await FetchAsync(hex, registration);
            // Finalize the lifetime when the shared fetch completes, regardless of which callers are still waiting:
            // shorten it when nothing useful came back, and only if this entry hasn't been replaced.
            if (!info.Found && _cache.TryGetValue(key, out var cur) && ReferenceEquals(cur.Value, lazy))
                _cache.TryUpdate(key, (time.GetUtcNow() + MissTtl, lazy!), cur);
            return info;
        });
        var entry = _cache.AddOrUpdate(key, _ => (now + HitTtl, lazy),
            (_, old) => old.Expires > now ? old : (now + HitTtl, lazy));
        return await entry.Value.Value.WaitAsync(ct);
    }

    private async Task<AircraftInfo> FetchAsync(string hex, string? registration)
    {
        // Shared fetch, so not tied to one caller's token. Each upstream gets its own deadline,
        // so a slow registry lookup can't use up the photo lookup's time.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var info = new AircraftInfo { Hex = hex, Registration = registration };
        try
        {
            info = ParseAdsbdb(hex, await adsbdb.GetStringAsync($"v0/aircraft/{hex}", cts.Token)) is { } parsed
                ? parsed with { Registration = parsed.Registration ?? registration, Found = true } : info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cts.IsCancellationRequested)
        { log.LogDebug(ex, "adsbdb lookup failed for {Hex}", hex); }

        using var photoCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var path = info.Registration is { Length: > 0 } reg
                ? $"pub/photos/reg/{Uri.EscapeDataString(reg)}" : $"pub/photos/hex/{hex}";
            var photo = await GetPhotoAsync(path, photoCts.Token);
            if (photo is null && info.Registration is not null)
                photo = await GetPhotoAsync($"pub/photos/hex/{hex}", photoCts.Token);
            info = info with { Photo = photo, Found = info.Found || photo is not null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || photoCts.IsCancellationRequested)
        { log.LogDebug(ex, "planespotters lookup failed for {Hex}", hex); }
        return info;
    }

    /// <summary>A 404 is a normal "no photo" answer, so it returns null instead of throwing (letting fallbacks run).</summary>
    private async Task<AircraftPhoto?> GetPhotoAsync(string path, CancellationToken ct)
    {
        using var resp = await planespotters.GetAsync(path, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return ParsePlanespotters(await resp.Content.ReadAsStringAsync(ct));
    }

    public static AircraftInfo? ParseAdsbdb(string hex, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var r) || r.ValueKind != JsonValueKind.Object ||
            !r.TryGetProperty("aircraft", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        return new AircraftInfo
        {
            Hex = hex,
            Registration = Str(a, "registration"),
            Manufacturer = Str(a, "manufacturer"),
            Type = Str(a, "type"),
            IcaoType = Str(a, "icao_type"),
            Owner = Str(a, "registered_owner"),
            OwnerCountry = Str(a, "registered_owner_country_name"),
            OperatorFlag = Str(a, "registered_owner_operator_flag_code"),
        };
    }

    public static AircraftPhoto? ParsePlanespotters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in photos.EnumerateArray())
        {
            var img = p.TryGetProperty("thumbnail_large", out var tl) ? tl : p.TryGetProperty("thumbnail", out var t) ? t : default;
            if (img.ValueKind != JsonValueKind.Object || Str(img, "src") is not { } src || !IsHttps(src)) continue;
            int? w = null, h = null;
            if (img.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Object)
            {
                if (size.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number) w = wv.GetInt32();
                if (size.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number) h = hv.GetInt32();
            }
            var link = Str(p, "link");
            return new AircraftPhoto(src, w, h, link is not null && IsHttps(link) ? link : null, Str(p, "photographer"), "Planespotters.net");
        }
        return null;
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
}
