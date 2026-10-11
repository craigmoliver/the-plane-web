using PlaneWeb.Core;

namespace PlaneWeb.Web;

public static class Fmt
{
    public static string Alt(Aircraft a, UnitSystem u)
    {
        if (a.OnGround) return "GND";
        if (a.AltitudeFt is not { } ft) return "---";
        return u == UnitSystem.Metric ? $"{ft * 0.3048:N0} m" : $"{ft:N0} ft";
    }

    public static string Speed(double? kt, UnitSystem u) => kt is not { } k ? "---"
        : u == UnitSystem.Metric ? $"{k * 1.852:N0} km/h" : $"{k * 1.15078:N0} mph";

    public static string Dist(double? nm, UnitSystem u) => nm is not { } n ? "---"
        : u == UnitSystem.Metric ? $"{n * 1.852:N1} km" : $"{n * 1.15078:N1} mi";

    public static string Heading(double? deg) => deg is not { } d ? "---" : $"{d:000}° {Geo.CompassPoint(d)}";

    public static string Climb(double? fpm) => fpm switch
    {
        > 300 => "▲",
        < -300 => "▼",
        null => "",
        _ => "▶",
    };

    public static string Ident(Aircraft a) => a.Callsign ?? a.Registration ?? a.Hex.ToUpperInvariant();

    public static string Type(Aircraft a) => a.Description ?? a.TypeCode ?? (a.IsHelicopter ? "HELICOPTER" : "UNKNOWN TYPE");

    /// <summary>ADS-B emitter category (e.g. A3) to a readable weight class; unknown codes are returned as-is.</summary>
    public static string? CategoryName(string? c) => c switch
    {
        null or "" => null,
        "A1" => "Light (< 15,500 lb)", "A2" => "Small (15,500–75,000 lb)", "A3" => "Large (75,000–300,000 lb)",
        "A4" => "High-vortex large (B757)", "A5" => "Heavy (> 300,000 lb)", "A6" => "High performance", "A7" => "Rotorcraft",
        "B1" => "Glider / sailplane", "B2" => "Lighter-than-air", "B4" => "Ultralight", "B6" => "UAV / drone",
        "C1" => "Surface emergency vehicle", "C2" => "Surface service vehicle",
        _ => c,
    };

    public static string Eta(DateTimeOffset? eta, DateTimeOffset now)
    {
        if (eta is not { } e) return "--:--";
        var mins = Math.Max(0, (int)Math.Round((e - now).TotalMinutes));
        return mins < 60 ? $"{mins} MIN" : $"{mins / 60}H {mins % 60:00}M";
    }
}
