using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlaneWeb.Infrastructure.Auth;
using PlaneWeb.Infrastructure.Data;

namespace PlaneWeb.Web;

public static class AuthSetup
{
    public const string MustChangePasswordClaim = "pw_change";
    public const string ExternalAccountClaim = "external";
    public const string LoginRateLimit = "login";
    /// <summary>Signed in, even with a temporary password (only for changing it).</summary>
    public const string SignedInPolicy = "SignedIn";
    public const string AdminPolicy = "Admin";

    public static void AddPlaneWebAuth(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var section = builder.Configuration.GetSection("PlaneWeb:Auth");
        services.Configure<AuthOptions>(section);
        var auth = section.Get<AuthOptions>() ?? new AuthOptions();

        services.AddIdentity<AppUser, IdentityRole>(AccountService.ConfigureIdentity)
            .AddEntityFrameworkStores<PlaneWebDbContext>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AppClaimsFactory>();

        // Disabling a user or "sign out everywhere" changes the security stamp; check it every minute.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = RevalidateInterval(builder.Configuration));
        services.ConfigureApplicationCookie(o =>
        {
            o.LoginPath = "/account/login";
            o.LogoutPath = "/account/logout";
            o.AccessDeniedPath = "/account/denied";
            o.ExpireTimeSpan = TimeSpan.FromDays(30); // long-lived "remember me" for wall displays
            o.SlidingExpiration = true;
            o.Cookie.Name = "planeweb.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax; // required for the Microsoft sign-in redirect
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // HTTPS in Azure / behind Caddy, HTTP allowed on a LAN
            // API calls get 401/403 instead of a redirect to the login page.
            o.Events.OnRedirectToLogin = ctx => ApiAware(ctx, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = ctx => ApiAware(ctx, StatusCodes.Status403Forbidden);
        });

        if (auth.Entra.Enabled)
        {
            services.AddAuthentication().AddOpenIdConnect(AccountService.EntraProvider, "Work account", o =>
            {
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.Authority = $"{auth.Entra.Instance.TrimEnd('/')}/{auth.Entra.TenantId}/v2.0";
                o.ClientId = auth.Entra.ClientId;
                o.ClientSecret = auth.Entra.ClientSecret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.CallbackPath = "/signin-oidc";
                o.Scope.Clear();
                foreach (var s in new[] { "openid", "profile", "email" }) o.Scope.Add(s);
                o.TokenValidationParameters.NameClaimType = "name";
                o.SaveTokens = false;
            });
        }

        if (auth.Google.Enabled)
        {
            services.AddAuthentication().AddGoogle(AccountService.GoogleProvider, "Google account", o =>
            {
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.ClientId = auth.Google.ClientId!;
                o.ClientSecret = auth.Google.ClientSecret!;
                o.CallbackPath = "/signin-google";
                o.SaveTokens = false;
                // Not mapped by default. The default UserInformationEndpoint is the v3 (OIDC-compliant)
                // endpoint, whose JSON field is "email_verified" (the older v2 endpoint instead used
                // "verified_email" — mapping that name here would leave the claim always unpopulated).
                o.ClaimActions.MapJsonKey("email_verified", "email_verified", System.Security.Claims.ClaimValueTypes.Boolean);
            });
        }

        // A temporary-password session only satisfies SignedInPolicy (the change-password page);
        // every other policy also requires the password to have been changed.
        static AuthorizationPolicyBuilder Full() => new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => !ctx.User.HasClaim(MustChangePasswordClaim, "1"));
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(Full().Build())     // [Authorize]
            .SetFallbackPolicy(Full().Build())    // endpoints without metadata, incl. the Blazor hub
            .AddPolicy(AdminPolicy, Full().RequireRole(Roles.Admin).Build())
            .AddPolicy(SignedInPolicy, p => p.RequireAuthenticatedUser());
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityStateProvider>();
        services.AddScoped<AccountService>();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                // Keep the 429: the friendly error page would re-run this POST as a page request.
                if (ctx.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } f) f.Enabled = false;
                return ValueTask.CompletedTask;
            };
            // Per client IP; generous because a company may share one public IP. Account lockout stops guessing.
            o.AddPolicy(LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
        });

        // Behind Azure Container Apps or a reverse proxy, trust X-Forwarded-* so redirects use https —
        // but only from the proxy's network. PlaneWeb:TrustedProxyNetworks overrides the private-network default.
        if (builder.Configuration.GetValue<bool>("PlaneWeb:TrustForwardedHeaders"))
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                o.ForwardLimit = 1; // only the nearest proxy's entry
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
                foreach (var n in TrustedProxyNetworks(builder.Configuration)) o.KnownIPNetworks.Add(n);
            });
    }

    /// <summary>Default: loopback and private ranges (Docker networks, Azure Container Apps' internal proxy).</summary>
    public static readonly string[] DefaultTrustedProxyNetworks =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7"];

    public static IReadOnlyList<System.Net.IPNetwork> TrustedProxyNetworks(IConfiguration c)
    {
        var configured = c.GetSection("PlaneWeb:TrustedProxyNetworks").Get<string[]>() is { Length: > 0 } list
            ? list : (c["PlaneWeb:TrustedProxyNetworks"] is { Length: > 0 } csv ? csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null);
        return (configured ?? DefaultTrustedProxyNetworks).Select(System.Net.IPNetwork.Parse).ToList();
    }

    /// <summary>How often sessions are re-checked against the database (default 60 s; tests use 0).</summary>
    public static TimeSpan RevalidateInterval(IConfiguration c) =>
        TimeSpan.FromSeconds(Math.Clamp(c.GetValue("PlaneWeb:Auth:RevalidateSeconds", 60), 0, 3600));

    private static Task ApiAware(RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> ctx, int status)
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/logos"))
        {
            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }

    public static async Task InitializeAuthAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        await accounts.EnsureRolesAsync();
        var authOptions = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        await accounts.EnsureBootstrapAdminAsync(authOptions);
        if (authOptions.Google.Enabled)
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlaneWebDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await AccountService.EnsureGoogleAllowlistSeededAsync(db, authOptions.Google, scope.ServiceProvider.GetRequiredService<TimeProvider>());
        }
    }

    /// <summary>Sends users with a temporary password to the change-password page before anything else.</summary>
    public static IApplicationBuilder UseMustChangePassword(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var p = ctx.Request.Path;
        if (ctx.User.HasClaim(MustChangePasswordClaim, "1") && HttpMethods.IsGet(ctx.Request.Method) &&
            !p.StartsWithSegments("/account") && !p.StartsWithSegments("/_framework") && !p.StartsWithSegments("/_blazor") &&
            !p.StartsWithSegments("/api") && !p.StartsWithSegments("/logos") &&
            !Path.HasExtension(p.Value) && !p.StartsWithSegments("/healthz"))
        {
            ctx.Response.Redirect("/account/change-password");
            return;
        }
        await next();
    });

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var account = app.MapGroup("/account");

        account.MapPost("/logout", async (HttpContext ctx, IAntiforgery af, SignInManager<AppUser> signIn) =>
        {
            // A stale token (e.g. another tab switched accounts) is a client error, not a 500.
            if (!await af.IsRequestValidAsync(ctx)) return PlainBadRequest(ctx);
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/account/login");
        }).AllowAnonymous();

        account.MapPost("/external", async (HttpContext ctx, IAntiforgery af, IOptions<AuthOptions> opt, string provider, string? returnUrl) =>
        {
            if (!await af.IsRequestValidAsync(ctx)) return PlainBadRequest(ctx);
            if (!IsEnabledProvider(provider, opt.Value)) return Results.NotFound();
            var props = new AuthenticationProperties
            {
                RedirectUri = $"/account/external-callback?provider={Uri.EscapeDataString(provider)}&returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}",
            };
            return Results.Challenge(props, [provider]);
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        account.MapGet("/external-callback", async (HttpContext ctx, string provider, string? returnUrl, AccountService accounts,
            SignInManager<AppUser> signIn, IOptions<AuthOptions> opt, IDbContextFactory<PlaneWebDbContext> dbFactory) =>
        {
            if (!IsEnabledProvider(provider, opt.Value))
                return Results.LocalRedirect("/account/login?error=" + Uri.EscapeDataString("Sign-in refused."));
            var result = await ctx.AuthenticateAsync(IdentityConstants.ExternalScheme);
            await ctx.SignOutAsync(IdentityConstants.ExternalScheme);
            if (!result.Succeeded) return Results.LocalRedirect("/account/login?error=" + Uri.EscapeDataString("Sign-in failed."));

            AppUser? user; string? error;
            if (provider == AccountService.EntraProvider)
            {
                if (ExternalIdentity.FromPrincipal(result.Principal) is not { } id)
                    return Results.LocalRedirect("/account/login?error=" + Uri.EscapeDataString("Microsoft sign-in failed."));
                (user, error) = await accounts.ProvisionExternalAsync(id, opt.Value.Entra);
            }
            else
            {
                if (GoogleIdentity.FromPrincipal(result.Principal) is not { } id)
                    return Results.LocalRedirect("/account/login?error=" + Uri.EscapeDataString("Google sign-in failed."));
                await using var db = await dbFactory.CreateDbContextAsync();
                (user, error) = await accounts.ProvisionGoogleAsync(id, db);
            }
            if (user is null)
                return Results.LocalRedirect("/account/login?error=" + Uri.EscapeDataString(error ?? "Sign-in refused."));
            await signIn.SignInAsync(user, isPersistent: true);
            await accounts.RecordSignInAsync(user);
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
    }

    private static bool IsEnabledProvider(string provider, AuthOptions o) =>
        (provider == AccountService.EntraProvider && o.Entra.Enabled) ||
        (provider == AccountService.GoogleProvider && o.Google.Enabled);

    /// <summary>400 without the friendly error page (which would re-run this POST as a page request).</summary>
    private static IResult PlainBadRequest(HttpContext ctx)
    {
        if (ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } f) f.Enabled = false;
        return Results.BadRequest();
    }

    /// <summary>Only same-site relative paths; anything else goes to the home page (prevents open redirects).</summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") &&
        !url.Any(char.IsControl) ? url : "/";

    public static string? UserId(this ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier);
}

/// <summary>Adds the display name and the must-change-password flag to the sign-in cookie.</summary>
public sealed class AppClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> o)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, o)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrWhiteSpace(user.DisplayName)) id.AddClaim(new Claim("display_name", user.DisplayName));
        if (user.MustChangePassword) id.AddClaim(new Claim(AuthSetup.MustChangePasswordClaim, "1"));
        if (user.IsExternal) id.AddClaim(new Claim(AuthSetup.ExternalAccountClaim, "1"));
        return id;
    }
}

/// <summary>Re-checks open Blazor connections every minute so disabled or signed-out users lose access promptly.</summary>
public sealed class RevalidatingIdentityStateProvider(ILoggerFactory lf, IServiceScopeFactory scopes, IOptions<IdentityOptions> o, IConfiguration config)
    : RevalidatingServerAuthenticationStateProvider(lf)
{
    protected override TimeSpan RevalidationInterval =>
        AuthSetup.RevalidateInterval(config) is var t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await IsStillValidAsync(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), state.User, o.Value);
    }

    /// <summary>False if the user was deleted, disabled, or had their sessions revoked (security stamp changed).</summary>
    public static async Task<bool> IsStillValidAsync(UserManager<AppUser> users, ClaimsPrincipal principal, IdentityOptions o)
    {
        if (principal.Identity?.IsAuthenticated != true) return false;
        var user = await users.GetUserAsync(principal);
        if (user is null || await users.IsLockedOutAsync(user)) return false;
        if (!users.SupportsUserSecurityStamp) return true;
        var stamp = principal.FindFirstValue(o.ClaimsIdentity.SecurityStampClaimType);
        return stamp == await users.GetSecurityStampAsync(user);
    }
}
