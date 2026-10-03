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
builder.Services.AddHealthChecks();
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapHealthChecks("/healthz");
app.MapGet("/logos/{icao}.png", async (string icao, AirlineLogoService logos, HttpContext ctx, CancellationToken ct) =>
{
    var path = await logos.GetLogoPathAsync(icao, ct);
    if (path is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(path, "image/png");
});
app.MapGet("/api/aircraft/{hex}", async (string hex, string? reg, string? callsign,
    AircraftInfoService info, PlaneWeb.Core.IRouteLookup routes, HttpContext ctx, CancellationToken ct) =>
{
    var details = await info.GetAsync(hex, string.IsNullOrWhiteSpace(reg) ? null : reg.Trim(), ct);
    if (details is null) return Results.BadRequest();
    PlaneWeb.Core.FlightRoute? route = null;
    if (!string.IsNullOrWhiteSpace(callsign))
        try { route = await routes.GetRouteAsync(callsign.Trim(), ct); } catch (HttpRequestException) { }
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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
