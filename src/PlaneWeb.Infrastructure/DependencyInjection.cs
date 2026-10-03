using PlaneWeb.Core;
using PlaneWeb.Infrastructure.Data;
using PlaneWeb.Infrastructure.Providers;
using PlaneWeb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PlaneWeb.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlaneWeb(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PlaneWebOptions>(config.GetSection("The Plane Web"));
        services.AddSingleton(TimeProvider.System);

        var cs = config.GetConnectionString("Default") ?? "Data Source=planeweb.db";
        services.AddDbContextFactory<PlaneWebDbContext>(o => o.UseSqlite(cs));

        const string ua = "PlaneWeb/1.0 (+self-hosted)";
        services.AddHttpClient<AdsbLolProvider>(c =>
        {
            c.BaseAddress = new Uri(config["PlaneWeb:AdsbLolBaseUrl"] ?? "https://api.adsb.lol/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();
        services.AddHttpClient<AdsbFiProvider>(c =>
        {
            c.BaseAddress = new Uri(config["PlaneWeb:AdsbFiBaseUrl"] ?? "https://opendata.adsb.fi/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();
        services.AddHttpClient<VrsRouteLookup>(c =>
        {
            c.BaseAddress = new Uri(config["PlaneWeb:RoutesBaseUrl"] ?? "https://vrs-standing-data.adsb.lol/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();

        services.AddHttpClient(nameof(AirlineLogoService), c =>
        {
            c.BaseAddress = new Uri(config["PlaneWeb:LogoBaseUrl"]
                ?? "https://raw.githubusercontent.com/Jxck-S/airline-logos/main/flightaware_logos/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();
        services.AddSingleton(sp => new AirlineLogoService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AirlineLogoService)),
            config["PlaneWeb:LogoCacheDir"] ?? Path.Combine(AppContext.BaseDirectory, "logo-cache"),
            sp.GetRequiredService<ILogger<AirlineLogoService>>()));

        services.AddSingleton<IRouteLookup>(sp => sp.GetRequiredService<VrsRouteLookup>());
        services.AddSingleton<IFlightDataProvider>(sp =>
        {
            var lol = sp.GetRequiredService<AdsbLolProvider>();
            var fi = sp.GetRequiredService<AdsbFiProvider>();
            var order = string.Equals(config["PlaneWeb:Provider"], "adsb.fi", StringComparison.OrdinalIgnoreCase)
                ? new IFlightDataProvider[] { fi, lol }
                : [lol, fi];
            return new FallbackFlightDataProvider(order, sp.GetRequiredService<ILogger<FallbackFlightDataProvider>>());
        });

        // Trace files are served by the adsb.lol website (not the API host) and may redirect.
        services.AddHttpClient<AdsbLolTraceClient>(c =>
        {
            c.BaseAddress = new Uri(config["PlaneWeb:TraceBaseUrl"] ?? "https://adsb.lol/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            c.DefaultRequestHeaders.Referrer = c.BaseAddress;
            c.Timeout = TimeSpan.FromSeconds(20);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        // Aircraft details (adsbdb.com) and photos (planespotters.net, which requires a contact URL in the User-Agent).
        var contact = config["PlaneWeb:ContactUrl"] is { Length: > 0 } cu ? cu : "https://github.com/craigmoliver/the-plane-web";
        services.AddHttpClient("adsbdb", c =>
        {
            c.BaseAddress = new Uri(config["PlaneWeb:AdsbdbBaseUrl"] ?? "https://api.adsbdb.com/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();
        services.AddHttpClient("planespotters", c =>
        {
            c.BaseAddress = new Uri(config["PlaneWeb:PlanespottersBaseUrl"] ?? "https://api.planespotters.net/");
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"PlaneWeb/1.0 (+{contact})");
        }).AddStandardResilienceHandler();
        services.AddSingleton(sp =>
        {
            var f = sp.GetRequiredService<IHttpClientFactory>();
            return new AircraftInfoService(f.CreateClient("adsbdb"), f.CreateClient("planespotters"),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AircraftInfoService>>());
        });

        services.AddSingleton<SettingsService>();
        services.AddSingleton<FlightStateStore>();
        services.AddSingleton<TrailStore>();
        services.AddSingleton<TraceBackfillService>();
        // Order matters: restore saved trails before the first poll.
        services.AddHostedService<TrailPersistenceService>();
        services.AddHostedService(sp => sp.GetRequiredService<TraceBackfillService>());
        services.AddHostedService<FlightPollingService>();
        return services;
    }
}
