namespace PlaneWeb.Core;

public static class Geo
{
    public const double EarthRadiusNm = 3440.065;

    private static double Rad(double d) => d * Math.PI / 180.0;
    private static double Deg(double r) => r * 180.0 / Math.PI;

    /// <summary>Great-circle distance in nautical miles.</summary>
    public static double DistanceNm(GeoPoint a, GeoPoint b)
    {
        var dLat = Rad(b.Lat - a.Lat);
        var dLon = Rad(b.Lon - a.Lon);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Initial bearing from a to b in degrees [0,360).</summary>
    public static double BearingDeg(GeoPoint a, GeoPoint b)
    {
        var y = Math.Sin(Rad(b.Lon - a.Lon)) * Math.Cos(Rad(b.Lat));
        var x = Math.Cos(Rad(a.Lat)) * Math.Sin(Rad(b.Lat)) -
                Math.Sin(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Cos(Rad(b.Lon - a.Lon));
        return (Deg(Math.Atan2(y, x)) + 360) % 360;
    }

    public static string CompassPoint(double deg)
    {
        string[] pts = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        // Half-sector offset + Floor: each sector is [start, end), so boundaries resolve consistently clockwise.
        return pts[(int)Math.Floor((((deg % 360) + 360) % 360 + 22.5) / 45.0) % 8];
    }

    /// <summary>Ray-casting point-in-polygon (lat/lon treated as planar; fine for local areas).</summary>
    public static bool InPolygon(GeoPoint p, IReadOnlyList<GeoPoint> poly)
    {
        if (poly.Count < 3) return false;
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (xi, yi) = (poly[i].Lon, poly[i].Lat);
            var (xj, yj) = (poly[j].Lon, poly[j].Lat);
            if ((yi > p.Lat) != (yj > p.Lat) &&
                p.Lon < (xj - xi) * (p.Lat - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Center (vertex average) and radius (nm) of a circle enclosing the polygon.</summary>
    public static (GeoPoint Center, double RadiusNm) BoundingCircle(IReadOnlyList<GeoPoint> poly)
    {
        if (poly.Count == 0) throw new ArgumentException("Empty polygon", nameof(poly));
        var c = new GeoPoint(poly.Average(p => p.Lat), poly.Average(p => p.Lon));
        return (c, poly.Max(p => DistanceNm(c, p)));
    }

    /// <summary>
    /// Progress (0..1) along a great-circle route, based on distance flown vs. remaining.
    /// </summary>
    public static (double Progress, double RemainingNm) RouteProgress(GeoPoint origin, GeoPoint dest, GeoPoint current)
    {
        var flown = DistanceNm(origin, current);
        var remaining = DistanceNm(current, dest);
        var total = flown + remaining;
        var progress = total <= 0 ? 1 : flown / total;
        return (Math.Clamp(progress, 0, 1), remaining);
    }

    /// <summary>
    /// Picks the leg of a (possibly multi-leg) route the aircraft is most plausibly flying,
    /// or null if the position is far off every leg (stale/incorrect route data).
    /// </summary>
    public static FlightRoute? ResolveLeg(FlightRoute route, GeoPoint position, double? trackDeg = null)
    {
        FlightRoute? best = null;
        var bestScore = double.MaxValue;
        for (var i = 0; i + 1 < route.Stops.Count; i++)
        {
            var (a, b) = (route.Stops[i], route.Stops[i + 1]);
            var direct = DistanceNm(a.Position, b.Position);
            var detour = DistanceNm(a.Position, position) + DistanceNm(position, b.Position) - direct;
            // Allow generous slack for departures/arrivals, holding, and great-circle vs. airway routing.
            if (detour > Math.Max(60, direct * 0.35)) continue;
            var score = detour;
            // Tie-break out-and-back routes (A-B-A) using heading toward the destination.
            if (trackDeg is { } t)
            {
                var diff = Math.Abs(((BearingDeg(position, b.Position) - t) % 360 + 540) % 360 - 180);
                score += diff / 4;
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = new FlightRoute(route.Callsign, a, b) { Stops = route.Stops };
            }
        }
        return best;
    }

    /// <summary>Naive ETA: remaining distance / ground speed, plus a small approach allowance.</summary>
    public static DateTimeOffset? EstimateArrival(double remainingNm, double? groundSpeedKt, DateTimeOffset now)
    {
        if (groundSpeedKt is not { } gs || gs < 50) return null;
        var hours = remainingNm / gs;
        var approachMinutes = remainingNm > 30 ? 8 : remainingNm / 30 * 8;
        return now.AddHours(hours).AddMinutes(approachMinutes);
    }
}
