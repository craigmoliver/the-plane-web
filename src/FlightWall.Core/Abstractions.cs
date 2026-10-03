namespace FlightWall.Core;

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
    public double CenterLat { get; set; } = 47.4502;   // KSEA default
    public double CenterLon { get; set; } = -122.3088;
    public double RadiusNm { get; set; } = 15;
    /// <summary>Polygon as JSON array of [lat,lon] pairs.</summary>
    public string PolygonJson { get; set; } = "[]";
    public List<string> TrackedFlights { get; set; } = [];
    public int MinAltitudeFt { get; set; } = 0;
    public int MaxAltitudeFt { get; set; } = 60000;
    public bool IncludeGround { get; set; } = false;
    public bool IncludeHelicopters { get; set; } = true;
    public bool IncludeLight { get; set; } = true;
    public int RotateSeconds { get; set; } = 8;
    public int MaxAreaFlights { get; set; } = 6;
    public UnitSystem Units { get; set; } = UnitSystem.Imperial;
    public string Title { get; set; } = "THE FLIGHT WALL";

    public const int MaxTracked = 5;

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
