using PlaneWeb.Core;

namespace PlaneWeb.Infrastructure.Services;

/// <summary>Pure filtering / enrichment logic, separated for testability.</summary>
public static class FlightProcessor
{
    public static IEnumerable<Aircraft> FilterArea(IEnumerable<Aircraft> aircraft, WallSettings s)
    {
        var poly = s.Shape == AreaShape.Polygon ? s.Polygon : [];
        foreach (var a in aircraft)
        {
            if (a.Position is not { } p) continue;
            if (a.OnGround && !s.IncludeGround) continue;
            if (!a.OnGround && a.AltitudeFt is { } alt && (alt < s.MinAltitudeFt || alt > s.MaxAltitudeFt)) continue;
            if (a.IsHelicopter && !s.IncludeHelicopters) continue;
            if (a.IsLight && !s.IncludeLight) continue;
            if (s.Shape == AreaShape.Polygon && poly.Count >= 3)
            {
                if (!Geo.InPolygon(p, poly)) continue;
            }
            else if (Geo.DistanceNm(s.Center, p) > s.RadiusNm) continue;
            yield return a;
        }
    }

    public static FlightView Enrich(Aircraft a, FlightRoute? route, GeoPoint? reference, DateTimeOffset now)
    {
        double? dist = null, bearing = null, progress = null, remaining = null;
        DateTimeOffset? eta = null;
        if (a.Position is { } p)
        {
            if (route is not null) route = Geo.ResolveLeg(route, p, a.TrackDeg);
            if (reference is { } r)
            {
                dist = Geo.DistanceNm(r, p);
                bearing = Geo.BearingDeg(r, p);
            }
            if (route is not null)
            {
                (progress, remaining) = Geo.RouteProgress(route.Origin.Position, route.Destination.Position, p);
                if (a.OnGround && remaining < 5) progress = 1;
                eta = a.OnGround ? null : Geo.EstimateArrival(remaining.Value, a.GroundSpeedKt, now);
            }
        }
        return new FlightView
        {
            Aircraft = a, Route = route, DistanceNm = dist, BearingDeg = bearing,
            Progress = progress, RemainingNm = remaining, EstimatedArrival = eta,
        };
    }
}
