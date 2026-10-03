using FlightWall.Core;
using FlightWall.Infrastructure.Data;
using FlightWall.Infrastructure.Providers;
using FlightWall.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlightWall.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFlightWall(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FlightWallOptions>(config.GetSection("FlightWall"));
        services.AddSingleton(TimeProvider.System);

        var cs = config.GetConnectionString("Default") ?? "Data Source=flightwall.db";
        services.AddDbContextFactory<FlightWallDbContext>(o => o.UseSqlite(cs));

        const string ua = "FlightWallWeb/1.0 (+self-hosted)";
        services.AddHttpClient<AdsbLolProvider>(c =>
        {
            c.BaseAddress = new Uri(config["FlightWall:AdsbLolBaseUrl"] ?? "https://api.adsb.lol/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();
        services.AddHttpClient<AdsbFiProvider>(c =>
        {
            c.BaseAddress = new Uri(config["FlightWall:AdsbFiBaseUrl"] ?? "https://opendata.adsb.fi/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();
        services.AddHttpClient<VrsRouteLookup>(c =>
        {
            c.BaseAddress = new Uri(config["FlightWall:RoutesBaseUrl"] ?? "https://vrs-standing-data.adsb.lol/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }).AddStandardResilienceHandler();

        services.AddSingleton<IRouteLookup>(sp => sp.GetRequiredService<VrsRouteLookup>());
        services.AddSingleton<IFlightDataProvider>(sp =>
        {
            var lol = sp.GetRequiredService<AdsbLolProvider>();
            var fi = sp.GetRequiredService<AdsbFiProvider>();
            var order = string.Equals(config["FlightWall:Provider"], "adsb.fi", StringComparison.OrdinalIgnoreCase)
                ? new IFlightDataProvider[] { fi, lol }
                : [lol, fi];
            return new FallbackFlightDataProvider(order, sp.GetRequiredService<ILogger<FallbackFlightDataProvider>>());
        });

        services.AddSingleton<SettingsService>();
        services.AddSingleton<FlightStateStore>();
        services.AddHostedService<FlightPollingService>();
        return services;
    }
}
