using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PlaneWeb.Infrastructure.Data;
using PlaneWeb.Web.Components.Pages.Account;

namespace PlaneWeb.Tests;

/// <summary>
/// A failed sign-in must not reveal whether the email has an account: same message for unknown, wrong-password
/// and locked accounts, and one password hash check each (Identity skips its own for locked accounts).
/// </summary>
public class LoginLockoutTests(AppFactory app) : IClassFixture<AppFactory>
{
    private sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();
        public int Verifications;
        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);
        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref Verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    [Fact]
    public async Task LockedAccount_LooksLikeAnUnknownEmail()
    {
        var hasher = new CountingHasher();
        var c = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IPasswordHasher<AppUser>>(hasher)))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        async Task<(string Html, int Hashes)> Attempt(string email, string password)
        {
            var before = hasher.Verifications;
            var r = await AuthTests.LoginAsync(c, email, password);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode); // not signed in
            return (await r.Content.ReadAsStringAsync(), hasher.Verifications - before);
        }

        var unknown = await Attempt("nobody@example.com", "whatever password");
        Assert.Contains(Login.FailedMessage, unknown.Html);
        Assert.Equal(1, unknown.Hashes);

        for (var i = 0; i < 5; i++) // MaxFailedAccessAttempts
        {
            var wrong = await Attempt(AppFactory.AdminEmail, $"wrong password {i}");
            Assert.Contains(Login.FailedMessage, wrong.Html);
            Assert.Equal(1, wrong.Hashes);
        }

        // Now locked: even the right password fails, with the same message and the same hashing work.
        var locked = await Attempt(AppFactory.AdminEmail, AppFactory.AdminPassword);
        Assert.Contains(Login.FailedMessage, locked.Html);
        Assert.DoesNotContain("locked or disabled", locked.Html);
        Assert.Equal(1, locked.Hashes);
    }
}
