namespace PlaneWeb.Core;

public interface IFlightDataProvider
{
    string Name { get; }
    Task<ProviderResult> GetAircraftNearAsync(GeoPoint center, double radiusNm, CancellationToken ct);
    Task<ProviderResult> GetByCallsignAsync(string callsign, CancellationToken ct);
}

/// <summary>Aircraft plus the provider that actually served them (per request, never shared state).</summary>
public sealed record ProviderResult(IReadOnlyList<Aircraft> Aircraft, string Source);

public interface IRouteLookup
{
    Task<FlightRoute?> GetRouteAsync(string callsign, CancellationToken ct);
}

/// <summary>Persisted user settings (single row).</summary>
public sealed class WallSettings
{
    public int Id { get; set; } = 1;
    public DisplayMode Mode { get; set; } = DisplayMode.Area;
    public AreaShape Shape { get; set; } = AreaShape.Radius;
    public double CenterLat { get; set; } = 34.1015;   // Woodstock, GA default
    public double CenterLon { get; set; } = -84.5194;
    public double RadiusNm { get; set; } = 25;
    /// <summary>Polygon as JSON array of [lat,lon] pairs.</summary>
    public string PolygonJson { get; set; } = "[]";
    public List<string> TrackedFlights { get; set; } = [];
    public int MinAltitudeFt { get; set; } = 0;
    public int MaxAltitudeFt { get; set; } = 60000;
    public bool IncludeGround { get; set; } = false;
    public bool IncludeHelicopters { get; set; } = true;
    public bool IncludeLight { get; set; } = true;
    public int RotateSeconds { get; set; } = 8;
    /// <summary>Rotate through pages of results; when off, only the first page (nearest aircraft) is shown.</summary>
    public bool AutoPage { get; set; } = true;
    public int MaxAreaFlights { get; set; } = 6;
    public UnitSystem Units { get; set; } = UnitSystem.Imperial;
    public string Title { get; set; } = "THE PLANE WEB";

    // Map page
    /// <summary>How many minutes of flight path to keep and draw.</summary>
    public int TrailMinutes { get; set; } = 15;
    /// <summary>Fetch earlier path from adsb.lol when an aircraft first appears.</summary>
    public bool TraceBackfill { get; set; } = true;
    /// <summary>Default base layer: streets, dark or satellite (the browser remembers the last choice).</summary>
    public string MapLayer { get; set; } = "streets";
    /// <summary>Show every aircraft in the area, or only those on the wall.</summary>
    public bool MapShowAll { get; set; } = true;

    public const int MaxTracked = 5;
    public const int MaxTrailMinutes = 60;
    public static readonly string[] MapLayers = ["streets", "dark", "satellite"];
    /// <summary>Largest radius the ADS-B providers accept in one query.</summary>
    public const double MaxQueryRadiusNm = 250;

    /// <summary>Returns a user-facing validation error, or null if the settings are usable.</summary>
    public string? Validate()
    {
        if (Shape == AreaShape.Polygon && Polygon is { Count: >= 3 } poly &&
            Geo.BoundingCircle(poly).RadiusNm > MaxQueryRadiusNm)
            return $"The drawn area is too large: it must fit within a {MaxQueryRadiusNm:0} nm radius. Draw a smaller shape.";
        return null;
    }

    public GeoPoint Center => new(CenterLat, CenterLon);

    public IReadOnlyList<GeoPoint> Polygon
    {
        get
        {
            try
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<double[][]>(PolygonJson) ?? [];
                return arr.Where(a => a.Length >= 2).Select(a => new GeoPoint(a[0], a[1])).ToList();
            }
            catch { return []; }
        }
    }
}
