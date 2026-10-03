using System.Text.RegularExpressions;

namespace FlightWall.Core;

/// <summary>Converts user-entered flight numbers (e.g. "UA 123") to ADS-B callsigns ("UAL123").</summary>
public static partial class CallsignNormalizer
{
    private static readonly Dictionary<string, string> IataToIcao = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AA"] = "AAL", ["UA"] = "UAL", ["DL"] = "DAL", ["WN"] = "SWA", ["AS"] = "ASA", ["B6"] = "JBU",
        ["NK"] = "NKS", ["F9"] = "FFT", ["G4"] = "AAY", ["HA"] = "HAL", ["SY"] = "SCX", ["QX"] = "QXE",
        ["OO"] = "SKW", ["YX"] = "RPA", ["MQ"] = "ENY", ["9E"] = "EDV", ["OH"] = "JIA", ["ZW"] = "AWI",
        ["AC"] = "ACA", ["WS"] = "WJA", ["PD"] = "POE", ["TS"] = "TSC", ["AM"] = "AMX", ["Y4"] = "VOI",
        ["VB"] = "VIV", ["BA"] = "BAW", ["VS"] = "VIR", ["U2"] = "EZY", ["FR"] = "RYR", ["LH"] = "DLH",
        ["AF"] = "AFR", ["KL"] = "KLM", ["IB"] = "IBE", ["AZ"] = "ITY", ["LX"] = "SWR", ["OS"] = "AUA",
        ["SN"] = "BEL", ["SK"] = "SAS", ["AY"] = "FIN", ["EI"] = "EIN", ["TP"] = "TAP", ["W6"] = "WZZ",
        ["VY"] = "VLG", ["EW"] = "EWG", ["DY"] = "NOZ", ["LO"] = "LOT", ["TK"] = "THY", ["EK"] = "UAE",
        ["QR"] = "QTR", ["EY"] = "ETD", ["SQ"] = "SIA", ["CX"] = "CPA", ["QF"] = "QFA", ["NZ"] = "ANZ",
        ["JL"] = "JAL", ["NH"] = "ANA", ["KE"] = "KAL", ["OZ"] = "AAR", ["CA"] = "CCA", ["MU"] = "CES",
        ["CZ"] = "CSN", ["BR"] = "EVA", ["CI"] = "CAL", ["AI"] = "AIC", ["LA"] = "LAN", ["AV"] = "AVA",
        ["CM"] = "CMP", ["ET"] = "ETH", ["SA"] = "SAA", ["VA"] = "VOZ", ["FX"] = "FDX", ["5X"] = "UPS",
    };

    [GeneratedRegex(@"^([A-Z0-9]{2})(\d{1,4}[A-Z]?)$")]
    private static partial Regex IataFlight();

    [GeneratedRegex(@"^([A-Z]{3})\d")]
    private static partial Regex IcaoAirlinePrefix();

    /// <summary>Airline ICAO designator from an airline callsign ("ASA1" → "ASA"); null for registrations etc.</summary>
    public static string? AirlineIcao(string? callsign)
    {
        var m = IcaoAirlinePrefix().Match((callsign ?? "").Trim().ToUpperInvariant());
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string Normalize(string input)
    {
        var s = new string((input ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length == 0) return s;
        // Already ICAO style (3 letters + digits) or a registration -> leave as is.
        var m = IataFlight().Match(s);
        if (m.Success && IataToIcao.TryGetValue(m.Groups[1].Value, out var icao))
            return icao + m.Groups[2].Value;
        return s;
    }
}
