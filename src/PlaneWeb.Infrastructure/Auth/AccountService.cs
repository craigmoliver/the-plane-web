using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using PlaneWeb.Infrastructure.Data;

namespace PlaneWeb.Infrastructure.Auth;

/// <summary>Sign-in options from configuration (section <c>PlaneWeb:Auth</c>).</summary>
public sealed class AuthOptions
{
    /// <summary>Allow email + password accounts created by an admin.</summary>
    public bool LocalLogin { get; set; } = true;
    /// <summary>Bootstrap local admin, created at startup if no account with this email exists.</summary>
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
    public EntraOptions Entra { get; set; } = new();
}

/// <summary>Microsoft Entra ID (work account) sign-in. Enabled when TenantId and ClientId are set.</summary>
public sealed class EntraOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    /// <summary>Work-account emails that are made admins on sign-in (in addition to the Entra "Admin" app role).</summary>
    public List<string> AdminEmails { get; set; } = [];
    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Identity details taken from a Microsoft sign-in.</summary>
public sealed record ExternalIdentity(string ObjectId, string TenantId, string? Email, string? Name, IReadOnlyCollection<string> Roles)
{
    public const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    public const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    public static ExternalIdentity? FromPrincipal(ClaimsPrincipal p)
    {
        string? Get(params string[] types) => types.Select(t => p.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var oid = Get(ObjectIdClaim, "oid");
        var tid = Get(TenantIdClaim, "tid");
        if (oid is null || tid is null) return null;
        var email = Get(ClaimTypes.Email, "email", "preferred_username", ClaimTypes.Upn, "upn");
        var name = Get("name", ClaimTypes.Name);
        var roles = p.FindAll(ClaimTypes.Role).Concat(p.FindAll("roles")).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ExternalIdentity(oid, tid, email, name, roles);
    }
}

public sealed class AccountService(UserManager<AppUser> users, RoleManager<IdentityRole> roles, TimeProvider time, ILogger<AccountService> log)
{
    public const string EntraProvider = "Microsoft";

    /// <summary>Password and lockout policy, shared by the app and tests.</summary>
    public static void ConfigureIdentity(IdentityOptions o)
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.User.RequireUniqueEmail = false; // work accounts may lack an email claim
    }

    public async Task EnsureRolesAsync()
    {
        foreach (var r in new[] { Roles.Admin, Roles.User })
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));
    }

    /// <summary>Creates the configured bootstrap admin if it doesn't exist yet.</summary>
    public async Task EnsureBootstrapAdminAsync(AuthOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.AdminEmail) || string.IsNullOrWhiteSpace(o.AdminPassword)) return;
        if (await users.FindByEmailAsync(o.AdminEmail) is not null) return;
        var r = await CreateLocalAsync(o.AdminEmail, "Administrator", o.AdminPassword, admin: true, mustChange: false);
        if (r.Succeeded) log.LogInformation("Created bootstrap admin {Email}", o.AdminEmail);
        else log.LogError("Could not create bootstrap admin: {Errors}", string.Join("; ", r.Errors.Select(e => e.Description)));
    }

    public async Task<IdentityResult> CreateLocalAsync(string email, string? name, string password, bool admin, bool mustChange = true)
    {
        var u = new AppUser
        {
            UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true, DisplayName = name,
            MustChangePassword = mustChange, CreatedAt = time.GetUtcNow(), LockoutEnabled = true,
        };
        var r = await users.CreateAsync(u, password);
        if (!r.Succeeded) return r;
        return await users.AddToRolesAsync(u, admin ? [Roles.User, Roles.Admin] : [Roles.User]);
    }

    /// <summary>
    /// Finds or creates the account for a Microsoft sign-in. Only the configured tenant is accepted.
    /// Admin is granted from the Entra "Admin" app role or the AdminEmails list; it is never revoked here.
    /// Returns null (with a reason) when sign-in must be refused.
    /// </summary>
    public async Task<(AppUser? User, string? Error)> ProvisionExternalAsync(ExternalIdentity id, EntraOptions o)
    {
        if (!string.Equals(id.TenantId, o.TenantId, StringComparison.OrdinalIgnoreCase))
            return (null, "That account is not part of this organization.");

        var user = await users.FindByLoginAsync(EntraProvider, id.ObjectId);
        if (user is null)
        {
            var userName = id.Email ?? $"{id.ObjectId}@entra";
            if (await users.FindByNameAsync(userName) is { } clash && !clash.IsExternal)
                return (null, $"A local account already uses {userName}. Ask an admin to remove it first.");
            user = new AppUser
            {
                UserName = userName, Email = id.Email, EmailConfirmed = true, DisplayName = id.Name,
                IsExternal = true, CreatedAt = time.GetUtcNow(), LockoutEnabled = true,
            };
            var r = await users.CreateAsync(user);
            if (r.Succeeded) r = await users.AddLoginAsync(user, new UserLoginInfo(EntraProvider, id.ObjectId, "Work account"));
            if (r.Succeeded) r = await users.AddToRoleAsync(user, Roles.User);
            if (!r.Succeeded) return (null, string.Join("; ", r.Errors.Select(e => e.Description)));
            log.LogInformation("Provisioned work account {Email}", userName);
        }

        if (await users.IsLockedOutAsync(user)) return (null, "This account has been disabled.");

        var admin = id.Roles.Contains(Roles.Admin) ||
                    (id.Email is not null && o.AdminEmails.Contains(id.Email, StringComparer.OrdinalIgnoreCase));
        if (admin && !await users.IsInRoleAsync(user, Roles.Admin)) await users.AddToRoleAsync(user, Roles.Admin);
        if (id.Name is not null && user.DisplayName != id.Name) user.DisplayName = id.Name;
        await users.UpdateAsync(user);
        return (user, null);
    }

    public async Task RecordSignInAsync(AppUser u)
    {
        u.LastSignInAt = time.GetUtcNow();
        await users.UpdateAsync(u);
    }

    /// <summary>A random password that satisfies the configured policy (mixed case + digits, 16 chars).</summary>
    public static string GenerateTemporaryPassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789";
        const string all = lower + upper + digits;
        var chars = new char[16];
        chars[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        for (var i = 3; i < chars.Length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
