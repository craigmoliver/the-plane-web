using FlightWall.Core;
using FlightWall.Infrastructure.Providers;
using FlightWall.Infrastructure.Services;

namespace FlightWall.Tests;

public class ParsingTests
{
    private const string AdsbLol = """
    {"ac":[
      {"hex":"ab962d","type":"adsb_icao","flight":"ASA1    ","r":"N846AK","t":"B38M","desc":"BOEING 737 MAX 8",
       "alt_baro":7800,"alt_geom":8125,"gs":240.3,"track":267.38,"baro_rate":-384,"squawk":"0551","category":"A3",
       "lat":47.45,"lon":-122.5},
      {"hex":"~abc123","flight":"N1  ","alt_baro":"ground","gs":3,"lat":47.44,"lon":-122.3,"category":"A1"},
      {"hex":"def456","alt_baro":1200,"category":"A7"},
      {"flight":"NOHEX"}
    ],"msg":"No error","now":1791049174000,"total":3}
    """;

    [Fact]
    public void ParsesAdsbLolFormat()
    {
        var list = ReadsbParser.Parse(AdsbLol);
        Assert.Equal(3, list.Count);

        var a = list[0];
        Assert.Equal("ASA1", a.Callsign);
        Assert.Equal("N846AK", a.Registration);
        Assert.Equal("B38M", a.TypeCode);
        Assert.Equal(7800, a.AltitudeFt);
        Assert.Equal(-384, a.VerticalRateFpm);
        Assert.False(a.OnGround);

        var g = list[1];
        Assert.Equal("abc123", g.Hex);
        Assert.True(g.OnGround);
        Assert.Equal(0, g.AltitudeFt);
        Assert.True(g.IsLight);

        Assert.True(list[2].IsHelicopter);
        Assert.Null(list[2].Position);
    }

    [Fact]
    public void ParsesAdsbFiAircraftKey() =>
        Assert.Single(ReadsbParser.Parse("""{"now":1,"aircraft":[{"hex":"a1","lat":1,"lon":2}]}"""));

    [Fact]
    public void ParsesRoute()
    {
        const string json = """
        {"callsign":"ASA1","airport_codes":"KDCA-KSEA","_airports":[
          {"name":"Ronald Reagan Washington National Airport","icao":"KDCA","iata":"DCA","location":"Washington","lat":38.8521,"lon":-77.037697},
          {"name":"Seattle Tacoma International Airport","icao":"KSEA","iata":"SEA","location":"Seattle","lat":47.449,"lon":-122.309}
        ]}
        """;
        var r = VrsRouteLookup.Parse(json)!;
        Assert.Equal("DCA", r.Origin.Code);
        Assert.Equal("SEA", r.Destination.Code);
        Assert.Equal("Seattle", r.Destination.City);
        Assert.Null(VrsRouteLookup.Parse("""{"callsign":"X","_airports":[]}"""));
    }
}

public class FlightProcessorTests
{
    private static Aircraft Ac(string hex, double lat, double lon, int? alt = 10000, string cat = "A3", bool ground = false) =>
        new() { Hex = hex, Lat = lat, Lon = lon, AltitudeFt = alt, Category = cat, OnGround = ground, GroundSpeedKt = 300 };

    [Fact]
    public void FiltersByRadiusAltitudeAndCategory()
    {
        var s = new WallSettings { CenterLat = 47.45, CenterLon = -122.3, RadiusNm = 10, MinAltitudeFt = 1000, MaxAltitudeFt = 20000, IncludeHelicopters = false };
        Aircraft[] input =
        [
            Ac("in", 47.46, -122.3),
            Ac("far", 48.5, -122.3),
            Ac("high", 47.46, -122.3, alt: 35000),
            Ac("heli", 47.46, -122.3, cat: "A7"),
            Ac("gnd", 47.45, -122.3, alt: 0, ground: true),
            new() { Hex = "nopos" },
        ];
        Assert.Equal(["in"], FlightProcessor.FilterArea(input, s).Select(a => a.Hex));
    }

    [Fact]
    public void FiltersByPolygon()
    {
        var s = new WallSettings { Shape = AreaShape.Polygon, PolygonJson = "[[47,-123],[47,-122],[48,-122],[48,-123]]" };
        Aircraft[] input = [Ac("in", 47.5, -122.5), Ac("out", 46.5, -122.5)];
        Assert.Equal(["in"], FlightProcessor.FilterArea(input, s).Select(a => a.Hex));
    }

    [Fact]
    public void EnrichComputesProgressAndEta()
    {
        var route = new FlightRoute("X",
            new Airport("KSEA", "SEA", "Seattle", "Seattle", 47.45, -122.31),
            new Airport("KPDX", "PDX", "Portland", "Portland", 45.59, -122.6));
        var now = DateTimeOffset.UtcNow;
        var v = FlightProcessor.Enrich(Ac("a", 46.5, -122.45), route, new GeoPoint(47.45, -122.31), now);
        Assert.InRange(v.Progress!.Value, 0.4, 0.6);
        Assert.NotNull(v.EstimatedArrival);
        Assert.True(v.EstimatedArrival > now);
        Assert.NotNull(v.DistanceNm);
    }
}
