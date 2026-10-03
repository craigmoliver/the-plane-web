using FlightWall.Infrastructure;
using FlightWall.Infrastructure.Data;
using FlightWall.Infrastructure.Providers;
using FlightWall.Web.Components;
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
builder.Services.AddFlightWall(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

// Apply migrations on startup (creates the SQLite file if missing).
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlightWallDbContext>>();
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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
