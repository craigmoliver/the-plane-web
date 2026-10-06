using System.Security.Claims;
using System.Text.Json;
using PlaneWeb.Infrastructure.Auth;
using PlaneWeb.Web;
using GoogleOptions = Microsoft.AspNetCore.Authentication.Google.GoogleOptions;

namespace PlaneWeb.Tests;

public class AuthSetupTests
{
    /// <summary>
    /// Regression test for the actual configured claim mapping, not just GoogleIdentity.FromPrincipal's
    /// claim-reading logic: runs the real ClaimActions against a sample v3/OIDC userinfo payload (the
    /// default UserInformationEndpoint's actual response shape), so a wrong JSON key name here (e.g.
    /// the v2-only "verified_email") would fail this test without needing a live OAuth round trip.
    /// </summary>
    [Fact]
    public void GoogleClaimActions_MapEmailVerified_FromTheV3UserinfoPayload()
    {
        var o = new GoogleOptions();
        AuthSetup.ConfigureGoogleClaimActions(o);

        using var userInfo = JsonDocument.Parse("""
            {
                "sub": "12345",
                "email": "pat@example.com",
                "email_verified": true,
                "name": "Pat"
            }
            """);
        var identity = new ClaimsIdentity();
        foreach (var action in o.ClaimActions)
            action.Run(userInfo.RootElement, identity, "Google");

        var principal = new ClaimsPrincipal(identity);
        var result = GoogleIdentity.FromPrincipal(principal);
        Assert.NotNull(result);
        Assert.True(result.EmailVerified);
    }

    [Fact]
    public void GoogleClaimActions_UnverifiedEmail_MapsToFalse()
    {
        var o = new GoogleOptions();
        AuthSetup.ConfigureGoogleClaimActions(o);

        using var userInfo = JsonDocument.Parse("""
            {
                "sub": "12345",
                "email": "pat@example.com",
                "email_verified": false,
                "name": "Pat"
            }
            """);
        var identity = new ClaimsIdentity();
        foreach (var action in o.ClaimActions)
            action.Run(userInfo.RootElement, identity, "Google");

        var result = GoogleIdentity.FromPrincipal(new ClaimsPrincipal(identity));
        Assert.NotNull(result);
        Assert.False(result.EmailVerified);
    }
}
