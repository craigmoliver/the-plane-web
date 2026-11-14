using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PlaneWeb.Infrastructure.Auth;
using PlaneWeb.Web;

namespace PlaneWeb.Tests;

/// <summary>
/// The external-callback endpoint must take the provider from the protected external ticket, not from the
/// query string. A test-only endpoint plants the external cookie that a real Google/Microsoft round trip would.
/// </summary>
public class ExternalCallbackTests(AppFactory app) : IClassFixture<AppFactory>
{
    private const string AllowedEmail = "allowed@example.com";

    private sealed class PlantExternalTicket : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => b =>
        {
            // Not Map(): that would scope the cookie's path to /test/external.
            b.Use(async (ctx, nextMw) =>
            {
                if (ctx.Request.Path != "/test/external") { await nextMw(); return; }
                var q = ctx.Request.Query;
                var id = new ClaimsIdentity("test");
                foreach (var name in new[] { "sub", "email", "email_verified", "oid", "tid" })
                    if (q[name] is { Count: > 0 } v) id.AddClaim(new Claim(name, v.ToString()));
                var props = new AuthenticationProperties();
                if (q["issued"] is { Count: > 0 } issued) props.Items[AuthSetup.LoginProviderKey] = issued.ToString();
                await ctx.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(id), props);
            });
            next(b);
        };
    }

    private HttpClient Client() => app.WithWebHostBuilder(b =>
    {
        b.UseSetting("PlaneWeb:Auth:Google:ClientId", "test-client");
        b.UseSetting("PlaneWeb:Auth:Google:ClientSecret", "test-secret");
        b.UseSetting("PlaneWeb:Auth:Google:AllowedEmails:0", AllowedEmail);
        b.ConfigureServices(s => s.AddTransient<IStartupFilter, PlantExternalTicket>());
    }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpResponseMessage> Callback(HttpClient c, string ticketQuery, string callbackQuery = "")
    {
        (await c.GetAsync("/test/external?" + ticketQuery)).EnsureSuccessStatusCode();
        return await c.GetAsync("/account/external-callback?returnUrl=%2Fwall" + callbackQuery);
    }

    private static string Location(HttpResponseMessage r) => Uri.UnescapeDataString(r.Headers.Location!.OriginalString);

    [Fact]
    public async Task GoogleTicket_SignsIn_WhateverTheQuerySays()
    {
        var c = Client();
        var r = await Callback(c, $"issued=Google&sub=g-123&email={AllowedEmail}&email_verified=true", "&provider=Microsoft");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/wall", Location(r));
        Assert.Contains(r.Headers.GetValues("Set-Cookie"), h => h.StartsWith("planeweb.auth="));
    }

    [Theory]
    [InlineData("")]           // no provider recorded (e.g. a ticket not made by our challenge)
    [InlineData("Microsoft")]  // a provider that is not enabled
    [InlineData("Bogus")]
    public async Task TicketWithoutAnEnabledProvider_IsRefused(string issued)
    {
        var c = Client();
        var r = await Callback(c, $"issued={issued}&sub=g-123&email={AllowedEmail}&email_verified=true", "&provider=Google");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/account/login?error=", Location(r));
        Assert.Contains("Sign-in refused.", await c.GetStringAsync(r.Headers.Location));
    }

    [Fact]
    public void ChallengeProperties_RecordTheProvider_AndASafeReturnUrl()
    {
        var p = AuthSetup.ChallengeProperties(AccountService.GoogleProvider, "//evil.example");
        Assert.Equal(AccountService.GoogleProvider, p.Items[AuthSetup.LoginProviderKey]);
        Assert.Equal("/account/external-callback?returnUrl=%2F", p.RedirectUri);
    }

    [Fact]
    public void IssuingProvider_OnlyReturnsEnabledProviders()
    {
        var o = new AuthOptions { Google = { ClientId = "id", ClientSecret = "secret" } };
        Assert.Equal("Google", AuthSetup.IssuingProvider(AuthSetup.ChallengeProperties("Google", null), o));
        Assert.Null(AuthSetup.IssuingProvider(AuthSetup.ChallengeProperties("Microsoft", null), o));
        Assert.Null(AuthSetup.IssuingProvider(new AuthenticationProperties(), o));
        Assert.Null(AuthSetup.IssuingProvider(null, o));
    }
}
