using PlaneWeb.Web;

namespace PlaneWeb.Tests;

public class FmtCategoryTests
{
    [Theory]
    [InlineData("A3", "Large (75,000–300,000 lb)")]
    [InlineData("A7", "Rotorcraft")]
    [InlineData("Z9", "Z9")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void CategoryName_MapsKnownCodes_AndPassesThroughUnknown(string? code, string? expected) =>
        Assert.Equal(expected, Fmt.CategoryName(code));
}
