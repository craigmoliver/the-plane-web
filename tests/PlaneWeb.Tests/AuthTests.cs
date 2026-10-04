using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace PlaneWeb.Tests;

/// <summary>Boots the real app against a temporary SQLite file with a bootstrap admin, no live feeds.</summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@example.com", AdminPassword = "correct horse 42";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "planeweb-it-" + Guid.NewGuid());

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        Directory.CreateDirectory(_dir);
        b.UseEnvironment("Production");
        b.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dir, "it.db")}");
        b.UseSetting("PlaneWeb:TrailsFile", Path.Combine(_dir, "trails.json"));
        b.UseSetting("PlaneWeb:Auth:AdminEmail", AdminEmail);
        b.UseSetting("PlaneWeb:Auth:AdminPassword", AdminPassword);
        b.UseSetting("PlaneWeb:Auth:RevalidateSeconds", "0"); // check sessions on every request
        // Unroutable feeds: polls fail fast instead of calling real services.
        foreach (var k in new[] { "AdsbLolBaseUrl", "AdsbFiBaseUrl", "RoutesBaseUrl", "TraceBaseUrl", "AdsbdbBaseUrl", "PlanespottersBaseUrl", "LogoBaseUrl" })
            b.UseSetting($"PlaneWeb:{k}", "http://127.0.0.1:9/");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

public class AuthTests(AppFactory app) : IClassFixture<AppFactory>
{
    private HttpClient Client() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Theory]
    [InlineData("/")]
    [InlineData("/wall")]
    [InlineData("/map")]
    [InlineData("/settings")]
    [InlineData("/admin/users")]
    public async Task Pages_RedirectAnonymousUsersToLogin(string path)
    {
        var r = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/account/login", r.Headers.Location!.PathAndQuery.Replace("http://localhost", ""));
    }

    [Theory]
    [InlineData("/api/aircraft/abc123")]
    [InlineData("/logos/DAL.png")]
    public async Task Api_Returns401ForAnonymous(string path)
    {
        var r = await Client().GetAsync(path);
        Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{r.StatusCode} -> {r.Headers.Location}");
    }

    [Fact]
    public async Task HealthAndLoginPage_AreOpen()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/account/login")).StatusCode);
    }

    [Fact]
    public async Task BlazorHub_RejectsAnonymous()
    {
        var r = await Client().PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect, $"got {r.StatusCode}");
    }

    internal static async Task<HttpResponseMessage> LoginAsync(HttpClient c, string email, string password)
    {
        var page = await (await c.GetAsync("/account/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return await c.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login",
            ["Input.Email"] = email, ["Input.Password"] = password,
        }));
    }

    [Fact]
    public async Task Admin_CanSignIn_AndReachPages()
    {
        var c = Client();
        var r = await LoginAsync(c, AppFactory.AdminEmail, AppFactory.AdminPassword);
        Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"login returned {r.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/aircraft/not-hex")).StatusCode); // authorized, reaches the handler
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        var c = Client();
        var r = await LoginAsync(c, AppFactory.AdminEmail, "wrong password 1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Incorrect email or password", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task TemporaryPassword_OnlyAllowsChangingIt()
    {
        var email = $"temp{Guid.NewGuid():N}@example.com";
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var r = await scope.ServiceProvider.GetRequiredService<PlaneWeb.Infrastructure.Auth.AccountService>()
                .CreateLocalAsync(email, null, "temporary password 1", admin: true, mustChange: true);
            Assert.True(r.Succeeded);
        }
        var c = Client();
        await LoginAsync(c, email, "temporary password 1");

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/account/change-password")).StatusCode);
        foreach (var path in new[] { "/settings", "/admin/users", "/map" })
        {
            var r = await c.GetAsync(path);
            Assert.True(r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.ToString().EndsWith("/account/change-password"),
                $"{path}: {r.StatusCode} {r.Headers.Location}");
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/aircraft/abc123")).StatusCode);
        // Not even an interactive connection (which never runs the redirect middleware).
        var hub = await c.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.True(hub.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, $"hub: {hub.StatusCode}");
    }

    [Fact]
    public async Task NonAdmin_CannotOpenAdminPage()
    {
        var email = $"user{Guid.NewGuid():N}@example.com";
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PlaneWeb.Infrastructure.Auth.AccountService>()
                .CreateLocalAsync(email, null, "regular password 1", admin: false, mustChange: false);
        var c = Client();
        await LoginAsync(c, email, "regular password 1");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings")).StatusCode);
        var r = await c.GetAsync("/admin/users");
        Assert.True(r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.ToString().Contains("/account/denied"), $"{r.StatusCode} {r.Headers.Location}");
    }

    private async Task<(HttpClient Client, string Email)> SignedInUserAsync()
    {
        var email = $"s{Guid.NewGuid():N}@example.com";
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PlaneWeb.Infrastructure.Auth.AccountService>()
                .CreateLocalAsync(email, null, "session password 1", admin: false, mustChange: false);
        var c = Client();
        await LoginAsync(c, email, "session password 1");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings")).StatusCode);
        return (c, email);
    }

    private async Task WithUser(string email, Func<Microsoft.AspNetCore.Identity.UserManager<PlaneWeb.Infrastructure.Data.AppUser>, PlaneWeb.Infrastructure.Data.AppUser, Task> f)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<PlaneWeb.Infrastructure.Data.AppUser>>();
        await f(um, (await um.FindByNameAsync(email))!);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("sign-out-everywhere")]
    [InlineData("delete")]
    public async Task ExistingSession_IsRevoked_OthersUnaffected(string action)
    {
        var (victim, victimEmail) = await SignedInUserAsync();
        var (control, _) = await SignedInUserAsync();

        await WithUser(victimEmail, async (um, u) =>
        {
            switch (action)
            {
                case "disable": await um.SetLockoutEndDateAsync(u, DateTimeOffset.MaxValue); await um.UpdateSecurityStampAsync(u); break;
                case "sign-out-everywhere": await um.UpdateSecurityStampAsync(u); break;
                case "delete": await um.DeleteAsync(u); break;
            }
        });

        var r = await victim.GetAsync("/settings");
        Assert.True(r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.ToString().Contains("/account/login"), $"{action}: {r.StatusCode} {r.Headers.Location}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/aircraft/abc123")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await control.GetAsync("/settings")).StatusCode); // unchanged user keeps access
    }

    [Fact]
    public void TrustedProxies_DefaultToPrivateNetworks_AndCanBeOverridden()
    {
        bool Trusted(IReadOnlyList<System.Net.IPNetwork> nets, string ip) => nets.Any(n => n.Contains(IPAddress.Parse(ip)));
        var defaults = PlaneWeb.Web.AuthSetup.TrustedProxyNetworks(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.True(Trusted(defaults, "172.18.0.3"));      // Docker network (Caddy)
        Assert.True(Trusted(defaults, "100.100.0.10"));    // Azure Container Apps internal
        Assert.False(Trusted(defaults, "8.8.8.8"));        // the internet can't spoof headers

        var custom = PlaneWeb.Web.AuthSetup.TrustedProxyNetworks(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PlaneWeb:TrustedProxyNetworks"] = "10.1.2.0/24" }).Build());
        Assert.True(Trusted(custom, "10.1.2.9"));
        Assert.False(Trusted(custom, "192.168.1.5"));
    }

    [Theory]
    [InlineData("https://evil.example/x", "/")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/\t/evil.example/x", "/")]
    [InlineData("/map", "/map")]
    public void ReturnUrl_IsLocalOnly(string input, string expected) =>
        Assert.Equal(expected, PlaneWeb.Web.AuthSetup.SafeReturnUrl(input));
}
