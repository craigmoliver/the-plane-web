using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlaneWeb.Infrastructure.Auth;
using PlaneWeb.Infrastructure.Data;

namespace PlaneWeb.Tests;

public class GoogleProvisioningTests
{
    private static (ServiceProvider Sp, AccountService Accounts, UserManager<AppUser> Users, PlaneWebDbContext Db) Build(TestDb db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PlaneWebDbContext>(_ => db.CreateDbContext());
        services.AddIdentityCore<AppUser>(AccountService.ConfigureIdentity).AddRoles<IdentityRole>().AddEntityFrameworkStores<PlaneWebDbContext>();
        services.AddScoped<AccountService>();
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();
        return (sp, scope.ServiceProvider.GetRequiredService<AccountService>(),
            scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<PlaneWebDbContext>());
    }

    private static async Task AllowAsync(PlaneWebDbContext db, string email, bool admin = false)
    {
        db.GoogleAllowedUsers.Add(new GoogleAllowedUser { Email = email, IsAdmin = admin, AddedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UnverifiedEmail_IsRefused()
    {
        using var db = new TestDb();
        var (sp, accounts, _, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        await AllowAsync(ctx, "pat@gmail.com");
        var (user, error) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-1", "pat@gmail.com", false, "Pat"), ctx);
        Assert.Null(user);
        Assert.Contains("verified", error);
        sp.Dispose();
    }

    [Fact]
    public async Task EmailNotOnAllowlist_IsRefused()
    {
        using var db = new TestDb();
        var (sp, accounts, _, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        var (user, error) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-1", "stranger@gmail.com", true, "Stranger"), ctx);
        Assert.Null(user);
        Assert.Contains("not allowed", error);
        sp.Dispose();
    }

    [Fact]
    public async Task FirstSignIn_CreatesUser_SecondReusesIt()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        await AllowAsync(ctx, "pat@gmail.com");
        var id = new GoogleIdentity("sub-1", "pat@gmail.com", true, "Pat");

        var (u1, e1) = await accounts.ProvisionGoogleAsync(id, ctx);
        Assert.Null(e1);
        Assert.True(u1!.IsExternal);
        Assert.True(await users.IsInRoleAsync(u1, Roles.User));
        Assert.False(await users.IsInRoleAsync(u1, Roles.Admin));

        var (u2, _) = await accounts.ProvisionGoogleAsync(id, ctx);
        Assert.Equal(u1.Id, u2!.Id);
        sp.Dispose();
    }

    [Fact]
    public async Task MatchingSingleLocalAccount_IsLinked()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        var r = await accounts.CreateLocalAsync("sam@gmail.com", "Sam", "a local password 1", admin: false);
        Assert.True(r.Succeeded);
        await AllowAsync(ctx, "sam@gmail.com");

        var (user, error) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-sam", "sam@gmail.com", true, "Sam"), ctx);
        Assert.Null(error);
        Assert.False(user!.IsExternal); // still the original local account
        Assert.Equal("sam@gmail.com", user.Email);

        // Second sign-in reuses the same account via the linked login.
        var (user2, _) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-sam", "sam@gmail.com", true, "Sam"), ctx);
        Assert.Equal(user.Id, user2!.Id);
        sp.Dispose();
    }

    [Fact]
    public async Task MultipleLocalAccountsSameEmail_IsRefused()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        // Usernames must be unique, but (as with Entra/work accounts) two accounts can share an Email.
        await users.CreateAsync(new AppUser { UserName = "one", Email = "dup@gmail.com" });
        await users.CreateAsync(new AppUser { UserName = "two", Email = "dup@gmail.com" });
        await AllowAsync(ctx, "dup@gmail.com");

        var (user, error) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-x", "dup@gmail.com", true, "X"), ctx);
        Assert.Null(user);
        Assert.Contains("Several accounts", error);
        sp.Dispose();
    }

    [Fact]
    public async Task AdminAllowlistEntry_GrantsAdmin()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        await AllowAsync(ctx, "boss@gmail.com", admin: true);

        var (user, _) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-boss", "boss@gmail.com", true, "Boss"), ctx);
        Assert.True(await users.IsInRoleAsync(user!, Roles.Admin));
        sp.Dispose();
    }

    [Fact]
    public async Task DisabledAccount_IsRefused()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        await AllowAsync(ctx, "gone@gmail.com");
        var (u, _) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-gone", "gone@gmail.com", true, "Gone"), ctx);
        await users.SetLockoutEndDateAsync(u!, DateTimeOffset.MaxValue);

        var (again, error) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-gone", "gone@gmail.com", true, "Gone"), ctx);
        Assert.Null(again);
        Assert.Contains("disabled", error);
        sp.Dispose();
    }

    [Fact]
    public async Task SeedingAllowlist_OnlyHappensOnce()
    {
        using var db = new TestDb();
        var (sp, _, _, ctx) = Build(db);
        var opts = new GoogleOptions
        {
            AdminEmails = ["admin1@example.com", "admin2@example.com"],
            AllowedEmails = ["user@example.com"],
        };
        await AccountService.EnsureGoogleAllowlistSeededAsync(ctx, opts, TimeProvider.System);
        Assert.Equal(3, await ctx.GoogleAllowedUsers.CountAsync());
        Assert.True(ctx.GoogleAllowedUsers.Any(x => x.Email == "admin1@example.com" && x.IsAdmin));
        Assert.True(ctx.GoogleAllowedUsers.Any(x => x.Email == "user@example.com" && !x.IsAdmin));

        // A later change (e.g. via the admin UI) must not be overwritten by seeding again.
        ctx.GoogleAllowedUsers.Add(new GoogleAllowedUser { Email = "new@gmail.com", AddedAt = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
        await AccountService.EnsureGoogleAllowlistSeededAsync(ctx, opts, TimeProvider.System);
        Assert.Equal(4, await ctx.GoogleAllowedUsers.CountAsync());
        sp.Dispose();
    }

    [Fact]
    public async Task EmailMatch_IsCaseInsensitiveAndLocalOnly()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        // Differently-cased email than Google reports; must still match via normalization.
        await accounts.CreateLocalAsync("Sam@Example.com", "Sam", "a local password 1", admin: false);
        // An Entra-provisioned (external) account sharing the email must not be matched/linked.
        var entraUser = new AppUser { UserName = "entra-x", Email = "sam@example.com", IsExternal = true };
        await users.CreateAsync(entraUser);
        await AllowAsync(ctx, "sam@example.com");

        var (user, error) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-sam", "sam@example.com", true, "Sam"), ctx);
        Assert.Null(error);
        Assert.NotEqual(entraUser.Id, user!.Id);
        Assert.False(user.IsExternal); // linked to the local account, not re-matched to the Entra one
        sp.Dispose();
    }

    [Fact]
    public async Task ToggleAdmin_AppliesImmediatelyToAlreadyLinkedAccount()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        await AllowAsync(ctx, "promote@example.com");
        var (user, _) = await accounts.ProvisionGoogleAsync(new GoogleIdentity("sub-p", "promote@example.com", true, "P"), ctx);
        Assert.False(await users.IsInRoleAsync(user!, Roles.Admin));

        var r1 = await accounts.SetGoogleAdminAsync("promote@example.com", admin: true);
        Assert.True(r1.Succeeded);
        Assert.True(await users.IsInRoleAsync(user!, Roles.Admin));

        var r2 = await accounts.SetGoogleAdminAsync("promote@example.com", admin: false);
        Assert.True(r2.Succeeded);
        Assert.False(await users.IsInRoleAsync(user!, Roles.Admin));
        sp.Dispose();
    }

    [Fact]
    public async Task ToggleAdmin_IgnoresAccountsWithoutAGoogleLogin()
    {
        using var db = new TestDb();
        var (sp, accounts, users, ctx) = Build(db);
        await accounts.EnsureRolesAsync();
        // A local account sharing the email but never signed in with Google (not provisioned/linked yet).
        await accounts.CreateLocalAsync("untouched@example.com", "U", "a local password 1", admin: false);

        var r = await accounts.SetGoogleAdminAsync("untouched@example.com", admin: true);
        Assert.True(r.Succeeded);
        var local = await users.FindByNameAsync("untouched@example.com");
        Assert.False(await users.IsInRoleAsync(local!, Roles.Admin));
        sp.Dispose();
    }
}
