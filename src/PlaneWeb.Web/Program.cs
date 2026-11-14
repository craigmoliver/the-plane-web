using PlaneWeb.Web;
using Microsoft.AspNetCore.DataProtection;
using PlaneWeb.Infrastructure;
using PlaneWeb.Infrastructure.Data;
using PlaneWeb.Infrastructure.Providers;
using PlaneWeb.Web.Components;
using Microsoft.EntityFrameworkCore;

// Lightweight container health probe (the runtime image has no curl/wget).
if (args.Contains("--healthcheck"))
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var resp = await http.GetAsync("http://localhost:8080/healthz");
        return resp.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddPlaneWeb(builder.Configuration);
builder.AddPlaneWebAuth();
builder.Services.AddHealthChecks();
builder.Services.AddScoped<ClientTime>();
// Persist data-protection keys (antiforgery/circuits) so restarts don't invalidate open pages.
if (builder.Configuration["PlaneWeb:KeysDir"] is { Length: > 0 } keysDir)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));

var app = builder.Build();

// Apply migrations on startup (creates the SQLite file if missing).
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlaneWebDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
await app.InitializeAuthAsync();

if (app.Configuration.GetValue<bool>("PlaneWeb:TrustForwardedHeaders")) app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Friendly error pages for browsers; API and image callers keep the raw status code (e.g. 401).
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/logos"),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseAuthentication();
app.UseMustChangePassword(); // before authorization, which would otherwise send these users to "access denied"
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapAccountEndpoints();
app.MapGet("/logos/{icao}.png", async (string icao, AirlineLogoService logos, HttpContext ctx, CancellationToken ct) =>
{
    var path = await logos.GetLogoPathAsync(icao, ct);
    if (path is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(path, "image/png");
});
app.MapGet("/api/aircraft/{hex}", async (string hex, string? reg, string? callsign,
    AircraftInfoService info, PlaneWeb.Core.IRouteLookup routes, PlaneWeb.Infrastructure.Services.PollerRegistry pollers,
    HttpContext ctx, CancellationToken ct) =>
{
    // Hints are only used when they match what the aircraft is broadcasting right now, so clients can't
    // drive arbitrary outbound lookups (photo searches, routes) or flush the caches with made-up values.
    var live = pollers.FindLive(hex);
    var details = await info.GetAsync(hex, LiveHint(reg, live?.Registration), ct);
    if (details is null) return Results.BadRequest();
    PlaneWeb.Core.FlightRoute? route = null;
    if (LiveHint(callsign, live?.Callsign) is { } cs)
        try { route = await routes.GetRouteAsync(cs, ct); } catch (HttpRequestException) { }
    ctx.Response.Headers.CacheControl = "private, max-age=300";
    return Results.Ok(new
    {
        details,
        route = route is null ? null : new
        {
            stops = route.Stops.Select(s => new { code = s.Code, s.Icao, s.Name, s.City }),
        },
    });
});
app.MapStaticAssets().AllowAnonymous(); // the login page needs CSS/JS before sign-in
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;

public partial class Program
{
    /// <summary>The requested hint (trimmed) if it matches the live value, ignoring case; otherwise null.</summary>
    public static string? LiveHint(string? requested, string? live) =>
        !string.IsNullOrWhiteSpace(requested) && live is not null &&
        string.Equals(requested.Trim(), live.Trim(), StringComparison.OrdinalIgnoreCase) ? requested.Trim() : null;
}
