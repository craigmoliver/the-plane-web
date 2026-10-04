using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlaneWeb.Infrastructure.Auth;
using PlaneWeb.Infrastructure.Data;

namespace PlaneWeb.Tests;

public class EntraProvisioningTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static (ServiceProvider Sp, AccountService Accounts, UserManager<AppUser> Users) Build(TestDb db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PlaneWebDbContext>(_ => db.CreateDbContext());
        services.AddIdentityCore<AppUser>(AccountService.ConfigureIdentity).AddRoles<IdentityRole>().AddEntityFrameworkStores<PlaneWebDbContext>();
        services.AddScoped<AccountService>();
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();
        return (sp, scope.ServiceProvider.GetRequiredService<AccountService>(), scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    private static EntraOptions Opts(params string[] admins) => new() { TenantId = Tenant, ClientId = "c", AdminEmails = [.. admins] };

    [Fact]
    public async Task FirstSignIn_CreatesUser_SecondReusesIt()
    {
        using var db = new TestDb();
        var (sp, accounts, users) = Build(db);
        await accounts.EnsureRolesAsync();
        var id = new ExternalIdentity("oid-1", Tenant, "pat@corp.com", "Pat", []);

        var (u1, e1) = await accounts.ProvisionExternalAsync(id, Opts());
        Assert.Null(e1);
        Assert.True(u1!.IsExternal);
        Assert.True(await users.IsInRoleAsync(u1, Roles.User));
        Assert.False(await users.IsInRoleAsync(u1, Roles.Admin));

        var (u2, _) = await accounts.ProvisionExternalAsync(id with { Email = "pat.renamed@corp.com" }, Opts());
        Assert.Equal(u1.Id, u2!.Id); // matched by object id, not email
        sp.Dispose();
    }

    [Fact]
    public async Task EmailRenameThenReassignment_GivesTheNewPersonTheirOwnAccount()
    {
        using var db = new TestDb();
        var (sp, accounts, _) = Build(db);
        await accounts.EnsureRolesAsync();
        var (first, _) = await accounts.ProvisionExternalAsync(new ExternalIdentity("oid-a", Tenant, "sam@corp.com", "Sam A", []), Opts());
        // Sam A is renamed; the old address is later given to someone else.
        var (renamed, _) = await accounts.ProvisionExternalAsync(new ExternalIdentity("oid-a", Tenant, "sam.a@corp.com", "Sam A", []), Opts());
        var (second, err) = await accounts.ProvisionExternalAsync(new ExternalIdentity("oid-b", Tenant, "sam@corp.com", "Sam B", []), Opts());

        Assert.Equal(first!.Id, renamed!.Id);
        Assert.Equal("sam.a@corp.com", renamed.Email);
        Assert.Null(err);
        Assert.NotEqual(first.Id, second!.Id);
        Assert.Equal(AccountService.ExternalUserName("oid-b"), second.UserName);
        sp.Dispose();
    }

    [Fact]
    public async Task OtherTenant_IsRefused()
    {
        using var db = new TestDb();
        var (sp, accounts, _) = Build(db);
        await accounts.EnsureRolesAsync();
        var (u, e) = await accounts.ProvisionExternalAsync(new ExternalIdentity("x", "22222222-2222-2222-2222-222222222222", "a@b.com", null, []), Opts());
        Assert.Null(u);
        Assert.NotNull(e);
        sp.Dispose();
    }

    [Fact]
    public async Task AdminFromAppRoleOrEmailList()
    {
        using var db = new TestDb();
        var (sp, accounts, users) = Build(db);
        await accounts.EnsureRolesAsync();
        var (byRole, _) = await accounts.ProvisionExternalAsync(new ExternalIdentity("o1", Tenant, "r@corp.com", null, ["Admin"]), Opts());
        var (byList, _) = await accounts.ProvisionExternalAsync(new ExternalIdentity("o2", Tenant, "Boss@Corp.com", null, []), Opts("boss@corp.com"));
        Assert.True(await users.IsInRoleAsync(byRole!, Roles.Admin));
        Assert.True(await users.IsInRoleAsync(byList!, Roles.Admin));
        sp.Dispose();
    }

    [Fact]
    public async Task DisabledOrLocalClash_IsRefused()
    {
        using var db = new TestDb();
        var (sp, accounts, users) = Build(db);
        await accounts.EnsureRolesAsync();
        await accounts.CreateLocalAsync("local@corp.com", null, "a local password 1", admin: false);
        var (clash, e1) = await accounts.ProvisionExternalAsync(new ExternalIdentity("o3", Tenant, "local@corp.com", null, []), Opts());
        Assert.Null(clash);
        Assert.Contains("local account", e1);

        var (u, _) = await accounts.ProvisionExternalAsync(new ExternalIdentity("o4", Tenant, "gone@corp.com", null, []), Opts());
        await users.SetLockoutEndDateAsync(u!, DateTimeOffset.MaxValue);
        var (again, e2) = await accounts.ProvisionExternalAsync(new ExternalIdentity("o4", Tenant, "gone@corp.com", null, []), Opts());
        Assert.Null(again);
        Assert.Contains("disabled", e2);
        sp.Dispose();
    }

    [Fact]
    public async Task BootstrapAdmin_ToleratesDuplicateEmails()
    {
        using var db = new TestDb();
        var (sp, accounts, _) = Build(db);
        await accounts.EnsureRolesAsync();
        await accounts.ProvisionExternalAsync(new ExternalIdentity("o1", Tenant, "admin@corp.com", null, []), Opts());
        await accounts.ProvisionExternalAsync(new ExternalIdentity("o2", Tenant, "admin@corp.com", null, []), Opts());
        // Two accounts now share the email; startup must not throw.
        await accounts.EnsureBootstrapAdminAsync(new AuthOptions { AdminEmail = "admin@corp.com", AdminPassword = "a password 123" });
        sp.Dispose();
    }

    [Fact]
    public void TemporaryPasswords_MeetPolicy()
    {
        for (var i = 0; i < 50; i++)
        {
            var p = AccountService.GenerateTemporaryPassword();
            Assert.Equal(16, p.Length);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
        }
    }
}
