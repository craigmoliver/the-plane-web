using FlightWall.Core;

namespace FlightWall.Tests;

public class GeoTests
{
    private static readonly GeoPoint Sea = new(47.4502, -122.3088);
    private static readonly GeoPoint Pdx = new(45.5887, -122.5975);

    [Fact]
    public void Distance_SeaToPdx_IsAbout113Nm()
    {
        Assert.InRange(Geo.DistanceNm(Sea, Pdx), 110, 116);
        Assert.Equal(0, Geo.DistanceNm(Sea, Sea), 6);
    }

    [Fact]
    public void Bearing_SeaToPdx_IsRoughlySouth()
    {
        var b = Geo.BearingDeg(Sea, Pdx);
        Assert.InRange(b, 180, 200);
        Assert.Equal("S", Geo.CompassPoint(b));
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(44, "NE")]
    [InlineData(359, "N")]
    [InlineData(270, "W")]
    public void CompassPoint(double deg, string expected) => Assert.Equal(expected, Geo.CompassPoint(deg));

    [Fact]
    public void InPolygon_Square()
    {
        GeoPoint[] sq = [new(0, 0), new(0, 1), new(1, 1), new(1, 0)];
        Assert.True(Geo.InPolygon(new(0.5, 0.5), sq));
        Assert.False(Geo.InPolygon(new(1.5, 0.5), sq));
        Assert.False(Geo.InPolygon(new(0.5, 0.5), sq[..2]));
    }

    [Fact]
    public void BoundingCircle_EnclosesAllVertices()
    {
        GeoPoint[] poly = [new(47, -122), new(47.5, -122), new(47.5, -121), new(47, -121)];
        var (c, r) = Geo.BoundingCircle(poly);
        Assert.All(poly, p => Assert.True(Geo.DistanceNm(c, p) <= r + 1e-9));
    }

    [Fact]
    public void RouteProgress_Midpoint_IsHalf()
    {
        var mid = new GeoPoint((Sea.Lat + Pdx.Lat) / 2, (Sea.Lon + Pdx.Lon) / 2);
        var (p, rem) = Geo.RouteProgress(Sea, Pdx, mid);
        Assert.InRange(p, 0.45, 0.55);
        Assert.InRange(rem, 50, 62);
    }

    [Fact]
    public void ResolveLeg_PicksActiveLeg_AndRejectsImplausibleRoutes()
    {
        var sea = new Airport("KSEA", "SEA", "Seattle", "Seattle", Sea.Lat, Sea.Lon);
        var pdx = new Airport("KPDX", "PDX", "Portland", "Portland", Pdx.Lat, Pdx.Lon);
        var lax = new Airport("KLAX", "LAX", "LA", "LA", 33.94, -118.41);
        var sfo = new Airport("KSFO", "SFO", "SF", "SF", 37.62, -122.38);
        var mid = new GeoPoint(46.5, -122.45);

        var roundTrip = new FlightRoute("X", sea, sea) { Stops = [sea, pdx, sea] };
        Assert.Equal("PDX", Geo.ResolveLeg(roundTrip, mid, trackDeg: 180)!.Destination.Code);
        Assert.Equal("SEA", Geo.ResolveLeg(roundTrip, mid, trackDeg: 0)!.Destination.Code);

        Assert.Null(Geo.ResolveLeg(new FlightRoute("Y", lax, sfo), Sea));
    }

    [Fact]
    public void EstimateArrival()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Assert.Null(Geo.EstimateArrival(100, null, now));
        Assert.Null(Geo.EstimateArrival(100, 20, now));
        var eta = Geo.EstimateArrival(400, 400, now)!.Value;
        Assert.Equal(68, (eta - now).TotalMinutes, 1); // 60 min + 8 approach
    }
}

public class CallsignNormalizerTests
{
    [Theory]
    [InlineData("UA123", "UAL123")]
    [InlineData("ua 123", "UAL123")]
    [InlineData("AS1", "ASA1")]
    [InlineData("B6 1234", "JBU1234")]
    [InlineData("ASA1", "ASA1")]
    [InlineData("UAL123", "UAL123")]
    [InlineData("N12345", "N12345")]
    [InlineData("N123AB", "N123AB")]
    [InlineData("  ", "")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, CallsignNormalizer.Normalize(input));
}

public class FmtTests
{
    [Fact]
    public void Eta_NeverNegative()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        Assert.Equal("0 MIN", FlightWall.Web.Fmt.Eta(now.AddHours(-3), now));
        Assert.Equal("1H 30M", FlightWall.Web.Fmt.Eta(now.AddMinutes(90), now));
    }
}

public class ReviewRound2Tests
{
    [Theory]
    [InlineData(22.4, "N")]
    [InlineData(22.5, "NE")]
    [InlineData(67.5, "E")]
    [InlineData(337.5, "N")]
    [InlineData(-45, "NW")]
    public void CompassBoundaries_AreConsistent(double deg, string expected) =>
        Assert.Equal(expected, Geo.CompassPoint(deg));

    [Fact]
    public void Validate_RejectsPolygonBeyondProviderRadius()
    {
        var small = new WallSettings { Shape = AreaShape.Polygon, PolygonJson = "[[47,-123],[47,-122],[48,-122]]" };
        var huge = new WallSettings { Shape = AreaShape.Polygon, PolygonJson = "[[30,-125],[30,-100],[49,-100],[49,-125]]" };
        Assert.Null(small.Validate());
        Assert.NotNull(huge.Validate());
        Assert.Null(new WallSettings { Shape = AreaShape.Radius, PolygonJson = huge.PolygonJson }.Validate());
    }
}

public class AirlineIcaoTests
{
    [Theory]
    [InlineData("ASA1", "ASA")]
    [InlineData("ual123 ", "UAL")]
    [InlineData("N12345", null)]
    [InlineData("N123AB", null)]
    [InlineData("CGABC", null)]
    [InlineData(null, null)]
    public void AirlineIcao(string? cs, string? expected) => Assert.Equal(expected, CallsignNormalizer.AirlineIcao(cs));
}

public class AirlineLogoRetryTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(10, 600)]
    public void RetryDelay_BacksOffAndCaps(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), FlightWall.Web.Components.AirlineLogo.RetryDelay(failures));
}
