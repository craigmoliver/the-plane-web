namespace PlaneWeb.Core;

public enum DisplayMode { Area = 0, Flights = 1 }
public enum AreaShape { Radius = 0, Polygon = 1 }
public enum UnitSystem { Imperial = 0, Metric = 1 }

public readonly record struct GeoPoint(double Lat, double Lon);

/// <summary>A live aircraft position as reported by an ADS-B aggregator.</summary>
public sealed record Aircraft
{
    public required string Hex { get; init; }
    public string? Callsign { get; init; }
    public string? Registration { get; init; }
    public string? TypeCode { get; init; }
    public string? Description { get; init; }
    public string? Operator { get; init; }
    public string? Category { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    /// <summary>Barometric altitude in feet; null when unknown; 0 when on ground.</summary>
    public int? AltitudeFt { get; init; }
    public bool OnGround { get; init; }
    /// <summary>Ground speed in knots.</summary>
    public double? GroundSpeedKt { get; init; }
    public double? TrackDeg { get; init; }
    public double? VerticalRateFpm { get; init; }
    public string? Squawk { get; init; }
    /// <summary>When this position was fetched from the feed (set by the poller).</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    public GeoPoint? Position => Lat is { } la && Lon is { } lo ? new GeoPoint(la, lo) : null;

    /// <summary>A7 = rotorcraft.</summary>
    public bool IsHelicopter => Category == "A7";
    /// <summary>A1 light, A2 small = typically GA / private.</summary>
    public bool IsLight => Category is "A1" or "A2" or "B1" or "B4";
}

public sealed record Airport(string Icao, string? Iata, string Name, string? City, double Lat, double Lon)
{
    public GeoPoint Position => new(Lat, Lon);
    public string Code => string.IsNullOrWhiteSpace(Iata) ? Icao : Iata!;
}

public sealed record FlightRoute(string Callsign, Airport Origin, Airport Destination)
{
    /// <summary>All airports in order for multi-leg routes (at least Origin and Destination).</summary>
    public IReadOnlyList<Airport> Stops { get; init; } = [Origin, Destination];
}

/// <summary>Aircraft enriched with computed fields for display.</summary>
public sealed record FlightView
{
    public required Aircraft Aircraft { get; init; }
    public FlightRoute? Route { get; init; }
    public double? DistanceNm { get; init; }
    public double? BearingDeg { get; init; }
    /// <summary>0..1 progress along route, null if unknown.</summary>
    public double? Progress { get; init; }
    public double? RemainingNm { get; init; }
    public DateTimeOffset? EstimatedArrival { get; init; }
}

/// <summary>A requested tracked flight and its current live state (if found).</summary>
public sealed record TrackedFlightView(string Requested, string Callsign, FlightView? Live);

/// <summary>One recorded position. <see cref="T"/> is Unix time in seconds; altitude null = unknown, 0 = ground.</summary>
public readonly record struct TrailPoint(double T, double Lat, double Lon, int? AltFt);

public sealed record WallSnapshot
{
    public DisplayMode Mode { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<FlightView> AreaFlights { get; init; } = [];
    public IReadOnlyList<TrackedFlightView> Tracked { get; init; } = [];
    /// <summary>Every aircraft that passed the area filter (or live tracked flights), for the map page.</summary>
    public IReadOnlyList<Aircraft> MapAircraft { get; init; } = [];
    public string? Error { get; init; }
    public string? Source { get; init; }

    public static WallSnapshot Empty { get; } = new() { UpdatedAt = DateTimeOffset.MinValue };
}
