using Microsoft.EntityFrameworkCore;
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
    public GoogleOptions Google { get; set; } = new();
}

/// <summary>Microsoft Entra ID (work account) sign-in. Enabled when TenantId and ClientId are set.</summary>
public sealed class EntraOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    /// <summary>
    /// Entra object IDs made admin on sign-in (in addition to the "Admin" app role). Object IDs, not emails:
    /// emails can be renamed and reassigned to someone else.
    /// </summary>
    public List<string> AdminObjectIds { get; set; } = [];
    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// Google sign-in. Enabled when ClientId and ClientSecret are set. Who may sign in is controlled by
/// <see cref="GoogleAllowedUser"/> rows in the database (seeded once from AllowedEmails/AdminEmails below
/// if the table is empty), not by config — so access can be changed on /admin/google-allowlist without a redeploy.
/// </summary>
public sealed class GoogleOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>Seeds the allowlist on first run only. Granted admin.</summary>
    public List<string> AdminEmails { get; set; } = [];
    /// <summary>Seeds the allowlist on first run only. Not granted admin.</summary>
    public List<string> AllowedEmails { get; set; } = [];
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
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

/// <summary>Identity details taken from a Google sign-in.</summary>
public sealed record GoogleIdentity(string Subject, string? Email, bool EmailVerified, string? Name)
{
    public static GoogleIdentity? FromPrincipal(ClaimsPrincipal p)
    {
        var sub = p.FindFirst("sub")?.Value ?? p.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (sub is null) return null;
        var email = p.FindFirst(ClaimTypes.Email)?.Value ?? p.FindFirst("email")?.Value;
        var verified = bool.TryParse(p.FindFirst("email_verified")?.Value, out var v) && v;
        var name = p.FindFirst("name")?.Value ?? p.FindFirst(ClaimTypes.Name)?.Value;
        return new GoogleIdentity(sub, email, verified, name);
    }
}

public sealed class AccountService(UserManager<AppUser> users, RoleManager<IdentityRole> roles, TimeProvider time, ILogger<AccountService> log)
{
    public const string EntraProvider = "Microsoft";
    public const string GoogleProvider = "Google";

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
        // Emails aren't unique (work accounts may share one), so FindByEmailAsync could throw; only existence matters.
        var normalized = users.NormalizeEmail(o.AdminEmail);
        var normalizedName = users.NormalizeName(o.AdminEmail);
        if (await users.Users.AnyAsync(u => u.NormalizedEmail == normalized || u.NormalizedUserName == normalizedName)) return;
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

        // Work accounts are keyed by their immutable Entra object id; emails can be renamed or reassigned.
        var userName = ExternalUserName(id.ObjectId);
        var user = await users.FindByLoginAsync(EntraProvider, id.ObjectId);
        if (user is null)
        {
            if (id.Email is not null && await users.FindByNameAsync(id.Email) is { IsExternal: false })
                return (null, $"A local account already uses {id.Email}. Ask an admin to remove it first.");
            user = new AppUser
            {
                UserName = userName, Email = id.Email, EmailConfirmed = true, DisplayName = id.Name,
                IsExternal = true, CreatedAt = time.GetUtcNow(), LockoutEnabled = true,
            };
            var r = await users.CreateAsync(user);
            if (r.Succeeded) r = await users.AddLoginAsync(user, new UserLoginInfo(EntraProvider, id.ObjectId, "Work account"));
            if (r.Succeeded) r = await users.AddToRoleAsync(user, Roles.User);
            if (!r.Succeeded) return (null, string.Join("; ", r.Errors.Select(e => e.Description)));
            log.LogInformation("Provisioned work account {Email}", id.Email ?? userName);
        }
        else
        {
            // Keep the username stable (older rows used the email) and the email current.
            if (user.UserName != userName) await users.SetUserNameAsync(user, userName);
            if (id.Email is not null && user.Email != id.Email) await users.SetEmailAsync(user, id.Email);
            user.EmailConfirmed = true;
        }

        if (await users.IsLockedOutAsync(user)) return (null, "This account has been disabled.");

        var admin = id.Roles.Contains(Roles.Admin) || o.AdminObjectIds.Contains(id.ObjectId, StringComparer.OrdinalIgnoreCase);
        if (admin && !await users.IsInRoleAsync(user, Roles.Admin)) await users.AddToRoleAsync(user, Roles.Admin);
        if (id.Name is not null && user.DisplayName != id.Name) user.DisplayName = id.Name;
        await users.UpdateAsync(user);
        return (user, null);
    }

    public static string ExternalUserName(string objectId) => $"entra-{objectId.ToLowerInvariant()}";

    public static string GoogleUserName(string subject) => $"google-{subject}";

    /// <summary>
    /// Finds or creates the account for a Google sign-in, linking by email to an existing local account
    /// when there's exactly one match. Refused when the email isn't verified, isn't on the allowlist,
    /// matches more than one local account, or the matched/found account is disabled.
    /// </summary>
    public async Task<(AppUser? User, string? Error)> ProvisionGoogleAsync(GoogleIdentity id, PlaneWebDbContext db)
    {
        if (!id.EmailVerified || string.IsNullOrWhiteSpace(id.Email))
            return (null, "Your Google account's email must be verified.");

        var email = id.Email.Trim();
        var allowed = await db.GoogleAllowedUsers.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Email == email.ToLowerInvariant());
        if (allowed is null) return (null, "This Google account is not allowed to sign in here. Ask an admin to add it.");

        var userName = GoogleUserName(id.Subject);
        var user = await users.FindByLoginAsync(GoogleProvider, id.Subject);
        if (user is null)
        {
            // Normalized, case-insensitive match, and only to local accounts (Entra/Google accounts are
            // already keyed by their own provider, not by email).
            var normalizedEmail = users.NormalizeEmail(email);
            var locals = await users.Users.Where(u => u.NormalizedEmail == normalizedEmail && !u.IsExternal).ToListAsync();
            if (locals.Count > 1)
                return (null, $"Several accounts use {email}; ask an admin to link the right one first.");

            if (locals.Count == 1)
            {
                user = locals[0];
                var r = await users.AddLoginAsync(user, new UserLoginInfo(GoogleProvider, id.Subject, "Google"));
                if (!r.Succeeded) return (null, string.Join("; ", r.Errors.Select(e => e.Description)));
                log.LogInformation("Linked Google sign-in to existing account {Email}", email);
            }
            else
            {
                user = new AppUser
                {
                    UserName = userName, Email = email, EmailConfirmed = true, DisplayName = id.Name,
                    IsExternal = true, CreatedAt = time.GetUtcNow(), LockoutEnabled = true,
                };
                var r = await users.CreateAsync(user);
                if (r.Succeeded) r = await users.AddLoginAsync(user, new UserLoginInfo(GoogleProvider, id.Subject, "Google"));
                if (r.Succeeded) r = await users.AddToRoleAsync(user, Roles.User);
                if (!r.Succeeded) return (null, string.Join("; ", r.Errors.Select(e => e.Description)));
                log.LogInformation("Provisioned Google account {Email}", email);
            }
        }

        if (await users.IsLockedOutAsync(user)) return (null, "This account has been disabled.");

        if (id.Name is not null && user.DisplayName != id.Name) user.DisplayName = id.Name;
        await users.UpdateAsync(user);

        if (allowed.IsAdmin && !await users.IsInRoleAsync(user, Roles.Admin)) await users.AddToRoleAsync(user, Roles.Admin);
        return (user, null);
    }

    /// <summary>
    /// Seeds the Google allowlist from config on first run only; later changes, including removing every
    /// row, happen via the admin UI and must never be reseeded on a later restart.
    /// </summary>
    public static async Task EnsureGoogleAllowlistSeededAsync(PlaneWebDbContext db, GoogleOptions o, TimeProvider time)
    {
        var state = await db.AuthSeedState.FirstOrDefaultAsync();
        if (state is null) { state = new AuthSeedState(); db.AuthSeedState.Add(state); }
        if (state.GoogleAllowlistSeeded) return;

        // Upgrading from a version that predates this marker: the allowlist table may already have rows
        // (e.g. from an earlier seed, or an admin's own additions) while this new, separate marker table
        // starts empty either way. Treat "already has rows" as "already seeded" rather than reseeding —
        // otherwise this would crash on the unique email index, or silently restore an entry an admin had
        // deliberately removed.
        if (await db.GoogleAllowedUsers.AnyAsync())
        {
            state.GoogleAllowlistSeeded = true;
            await db.SaveChangesAsync();
            return;
        }

        var now = time.GetUtcNow();
        var rows = o.AdminEmails.Select(e => (Email: e, Admin: true))
            .Concat(o.AllowedEmails.Select(e => (Email: e, Admin: false)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.Email.Trim().ToLowerInvariant())
            .Select(g => new GoogleAllowedUser { Email = g.Key, IsAdmin = g.Any(x => x.Admin), AddedBy = "seed", AddedAt = now });
        db.GoogleAllowedUsers.AddRange(rows);
        state.GoogleAllowlistSeeded = true;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Grants admin immediately to any account already linked to this email via Google (normally admin is
    /// only (re-)applied at the next sign-in). Only touches accounts with an existing Google login for this
    /// email — not every local account that happens to share it. Never removes the role: role membership
    /// alone can't tell a Google-sourced grant from one made on /admin/users or by Entra, so an automatic
    /// demotion here could silently strip an unrelated grant. Removing admin is done on /admin/users, same
    /// as Entra.
    /// </summary>
    public async Task<IdentityResult> GrantGoogleAdminAsync(string email)
    {
        var normalized = users.NormalizeEmail(email);
        var candidates = await users.Users.Where(u => u.NormalizedEmail == normalized).ToListAsync();
        foreach (var u in candidates)
        {
            var logins = await users.GetLoginsAsync(u);
            if (!logins.Any(l => l.LoginProvider == GoogleProvider)) continue;
            if (await users.IsInRoleAsync(u, Roles.Admin)) continue;
            var r = await users.AddToRoleAsync(u, Roles.Admin);
            if (!r.Succeeded) return r;
            await users.UpdateSecurityStampAsync(u); // role change applies on next check (within RevalidateSeconds)
        }
        return IdentityResult.Success;
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
