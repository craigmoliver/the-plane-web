# The Plane Web (FlightWall)

A self-hosted web version of a FlightWall-style LED flight display, built with .NET 10 Blazor and packaged as a Docker container.

- **`/wall`** – fullscreen LED-board display for a TV/kiosk (double-click for fullscreen).
- **`/settings`** – pick the mode, draw the area on a map, add tracked flights, filters, units.
- **`/healthz`** – health check.

## Modes (one at a time)
- **Area** – aircraft within a radius or a drawn polygon, with route, type, altitude, speed, distance and bearing.
- **Flights** – up to 5 flight numbers/callsigns (`UA123` is converted to `UAL123`) with route progress and an estimated ETA.

## Data sources (free, no API key)
- Live positions: [adsb.lol](https://api.adsb.lol), with [adsb.fi](https://opendata.adsb.fi) as fallback.
- Routes: adsb.lol VRS standing data. Routes are community-sourced and can be missing or wrong.
- ETA is estimated from distance remaining ÷ ground speed; it is not an airline schedule.

## Run with Docker
```bash
docker compose up -d --build
# open http://localhost:8080/settings
```
Settings are stored in SQLite at `/data/flightwall.db` (named volume `flightwall-data`).

| Env var | Default | Notes |
|---|---|---|
| `FlightWall__Provider` | `adsb.lol` | `adsb.lol` or `adsb.fi` (the other is the fallback) |
| `FlightWall__PollSeconds` | `5` | minimum 2 |
| `ConnectionStrings__Default` | `Data Source=/data/flightwall.db` | |
| `TZ` | – | wall clock timezone |

No authentication is built in, so run it on a trusted network.

## Develop
```bash
dotnet run --project src/FlightWall.Web
dotnet test
dotnet tool restore && dotnet ef migrations add <Name> -p src/FlightWall.Infrastructure -s src/FlightWall.Infrastructure -o Data/Migrations
```

## Layout
```
src/FlightWall.Core            models, geo math, callsign normalization
src/FlightWall.Infrastructure  ADS-B providers, route lookup, EF Core SQLite, polling service
src/FlightWall.Web             Blazor UI (wall, settings), Leaflet interop
tests/FlightWall.Tests         xUnit tests
```
