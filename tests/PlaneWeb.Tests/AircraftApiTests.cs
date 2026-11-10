namespace PlaneWeb.Tests;

/// <summary>/api/aircraft only forwards registration/callsign hints that match the live aircraft.</summary>
public class AircraftApiTests
{
    [Theory]
    [InlineData("N12345", "N12345", "N12345")]
    [InlineData(" n12345 ", "N12345", "n12345")]   // trimmed, case-insensitive (the lookup normalizes case)
    [InlineData("ASA1", "ASA1 ", "ASA1")]
    [InlineData("N99999", "N12345", null)]         // made-up value: not forwarded
    [InlineData("N12345", null, null)]             // aircraft not live (or not broadcasting one)
    [InlineData(null, "N12345", null)]
    [InlineData("  ", "N12345", null)]
    public void LiveHint_OnlyPassesValuesTheAircraftIsBroadcasting(string? requested, string? live, string? expected) =>
        Assert.Equal(expected, Program.LiveHint(requested, live));
}
