using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using PlaneWeb.Web;

namespace PlaneWeb.Tests;

/// <summary>The login page only shows error messages the app itself issued.</summary>
public class LoginErrorTests(AppFactory app) : IClassFixture<AppFactory>
{
    private HttpClient Client() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public void ErrorToken_RoundTrips_AndRejectsTampering()
    {
        var dp = new EphemeralDataProtectionProvider();
        var url = AuthSetup.LoginErrorUrl(dp, "A local account already uses a@b.c.", TimeProvider.System);
        var token = Uri.UnescapeDataString(url["/account/login?error=".Length..]);
        Assert.Equal("A local account already uses a@b.c.", AuthSetup.ReadLoginError(dp, token));
        // Flip one character in the middle (the last one may only carry unused padding bits).
        var i = token.Length / 2;
        var tampered = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];
        Assert.Null(AuthSetup.ReadLoginError(dp, tampered));
        Assert.Null(AuthSetup.ReadLoginError(dp, "Your account is locked, call 555-0100"));
        Assert.Null(AuthSetup.ReadLoginError(new EphemeralDataProtectionProvider(), token)); // other keys
        Assert.Null(AuthSetup.ReadLoginError(dp, null));
    }

    [Fact]
    public async Task CraftedErrorText_IsNotShown()
    {
        var r = await Client().GetAsync("/account/login?error=" + Uri.EscapeDataString("Call 555-0100 to unlock your account"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Call 555-0100 to unlock", html); // (it still appears URL-encoded in the form action)
        Assert.Contains("Sign-in failed. Please try again.", html);
    }

    [Fact]
    public async Task IssuedError_IsShown()
    {
        var dp = (IDataProtectionProvider)app.Services.GetService(typeof(IDataProtectionProvider))!;
        var r = await Client().GetAsync(AuthSetup.LoginErrorUrl(dp, "This account has been disabled.", TimeProvider.System));
        Assert.Contains("This account has been disabled.", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoError_ShowsNoMessage()
    {
        var html = await Client().GetStringAsync("/account/login");
        Assert.DoesNotContain("role=\"alert\"", html);
    }
}
