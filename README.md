# The Plane Web

A self-hosted web version of a LED-style flight display, built with .NET 10 Blazor and packaged as a Docker container.

- **`/wall`** – fullscreen LED-board display for a TV/kiosk (double-click for fullscreen).
- **`/map`** – live map of the area with each aircraft's flight path (last 15 minutes by default).
- **`/settings`** – pick the mode, draw the area on a map, add tracked flights, filters, units, map options.
- **`/healthz`** – health check.

## Modes (one at a time)
- **Area** – aircraft within a radius or a drawn polygon, with route, type, altitude, speed, distance and bearing.
- **Flights** – up to 5 flight numbers/callsigns (`UA123` is converted to `UAL123`) with route progress and an estimated ETA.

## Live map (`/map`)
- Shows every aircraft in the area; the wall's first page (nearest *Flights per page*) is highlighted with labels. Turn off *Show all aircraft* in settings to show only those.
- Flight paths are colored by altitude (legend bottom-left) and fade with age. Markers glide between polls using speed and heading.
- Map styles: Streets (OpenStreetMap), Dark (CARTO), Satellite (Esri, with optional place labels). Each browser remembers its last choice.
- When an aircraft first appears, its earlier path is loaded from adsb.lol's trace files (`adsb.lol/data/traces/...`). That endpoint is unofficial: failures are ignored, requests are spaced 2 s apart and pause for 60 s if rate-limited. Turn it off in settings if needed.
- Trails are saved to `/data/trails.json` every minute and on shutdown, and restored on start (stale ones dropped).
- Click an aircraft to open a details panel: photo of that airframe by registration (Planespotters.net, credited and linked), manufacturer/model, owner and country (adsbdb.com), route, live altitude/speed/heading/squawk, and links to adsb.lol, Planespotters, FlightAware and the FAA registry. Esc or clicking the map closes it.
- ⤢ fits the area, ⛶ toggles fullscreen.

## Data sources (free, no API key)
- Live positions: [adsb.lol](https://api.adsb.lol), with [adsb.fi](https://opendata.adsb.fi) as fallback.
- Routes: adsb.lol VRS standing data. Routes are community-sourced and can be missing or wrong.
- ETA is estimated from distance remaining ÷ ground speed; it is not an airline schedule.

## Run with Docker
```bash
docker compose up -d --build
# open http://<server>:8095/settings  (host port is set in docker-compose.yml)
```
Settings are stored in SQLite at `/data/planeweb.db` and flight trails in `/data/trails.json` (named volume `planeweb-data`).

| Env var | Default | Notes |
|---|---|---|
| `PlaneWeb__Provider` | `adsb.lol` | `adsb.lol` or `adsb.fi` (the other is the fallback) |
| `PlaneWeb__PollSeconds` | `5` | minimum 2 |
| `PlaneWeb__TrailsFile` | `/data/trails.json` | saved flight paths |
| `PlaneWeb__ContactUrl` | repo URL | contact URL sent to planespotters.net (their API requires one) |
| `PlaneWeb__TraceMinIntervalMs` | `2000` | gap between history lookups (min 250) |
| `ConnectionStrings__Default` | `Data Source=/data/planeweb.db` | |
| `TZ` | – | wall clock timezone |

No authentication is built in, so run it on a trusted network.

## Develop
```bash
dotnet run --project src/PlaneWeb.Web
dotnet test
dotnet tool restore && dotnet ef migrations add <Name> -p src/PlaneWeb.Infrastructure -s src/PlaneWeb.Infrastructure -o Data/Migrations
```

## Layout
```
src/PlaneWeb.Core            models, geo math, callsign normalization
src/PlaneWeb.Infrastructure  ADS-B providers, route lookup, EF Core SQLite, polling service
src/PlaneWeb.Web             Blazor UI (wall, settings), Leaflet interop
tests/PlaneWeb.Tests         xUnit tests
```
