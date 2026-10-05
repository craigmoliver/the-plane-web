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

## Sign-in and users
Every page requires sign-in (only `/healthz` and the sign-in page are open).
- **Local accounts:** an admin creates them on `/admin/users` and gets a one-time temporary password; the user must choose a new one at first sign-in. 5 wrong passwords lock the account for 15 minutes.
- **Work accounts (Microsoft Entra ID):** anyone in your organization can sign in once configured. See [docs/entra.md](docs/entra.md).
- **Roles:** Admin (manage users) and User. The first admin comes from `PlaneWeb__Auth__AdminEmail` / `PlaneWeb__Auth__AdminPassword`.
- **Per-user settings:** each person has their own area, filters, mode and map options. New users start from the default settings (the ones from before sign-in was added). People viewing the same area share one data feed.
- **Wall displays:** tick *Keep me signed in* for a 30-day session.
- Admins can disable, reset, promote or **sign out everywhere**; open pages lose access within a minute.

## Run with Docker
```bash
cp .env.example .env   # set PlaneWeb__Auth__AdminEmail / AdminPassword
docker compose up -d --build
# open http://<server>:8095/settings  (host port is set in docker-compose.yml)
```
Optional overrides:
- PostgreSQL instead of SQLite: `docker compose -f docker-compose.yml -f docker-compose.postgres.yml up -d --build`
- HTTPS with Caddy (needed for Microsoft sign-in except on localhost): add `-f docker-compose.https.yml` and set `PLANEWEB_HOST` in `.env`.

Settings are stored in SQLite at `/data/planeweb.db` and flight trails in `/data/trails.json` (named volume `planeweb-data`).

| Env var | Default | Notes |
|---|---|---|
| `PlaneWeb__Provider` | `adsb.lol` | `adsb.lol` or `adsb.fi` (the other is the fallback) |
| `PlaneWeb__PollSeconds` | `5` | minimum 2 |
| `PlaneWeb__TrailsFile` | `/data/trails.json` | saved flight paths |
| `PlaneWeb__ContactUrl` | repo URL | contact URL sent to planespotters.net (their API requires one) |
| `PlaneWeb__TraceMinIntervalMs` | `2000` | gap between history lookups (min 250) |
| `PlaneWeb__Database` | `Sqlite` | `Sqlite` or `Postgres` |
| `PlaneWeb__Postgres__Host` / `__Port` / `__Database` / `__Username` / `__Password` | – | Postgres settings (alternative to a connection string; passwords needn't be escaped) |
| `PlaneWeb__Auth__AdminEmail` / `__AdminPassword` | – | first admin, created if missing |
| `PlaneWeb__Auth__LocalLogin` | `true` | allow email/password accounts |
| `PlaneWeb__Auth__Entra__TenantId` / `__ClientId` / `__ClientSecret` | – | Microsoft sign-in; see docs/entra.md |
| `PlaneWeb__TrustForwardedHeaders` | `false` | `true` behind a reverse proxy |
| `PlaneWeb__TrustedProxyNetworks` | private ranges | comma-separated CIDRs allowed to send X-Forwarded-*; keep the app's own port unreachable except via the proxy |
| `ConnectionStrings__Default` | `Data Source=/data/planeweb.db` | |
| `TZ` | – | wall clock timezone |


## Deploy to Azure
See [docs/azure.md](docs/azure.md): a small Linux VM running this same Docker Compose setup (SQLite + Caddy for HTTPS), deployed by GitHub Actions on every push to `main`.

## Develop
```bash
PlaneWeb__Auth__AdminEmail=me@example.com PlaneWeb__Auth__AdminPassword=dev-password-1 dotnet run --project src/PlaneWeb.Web
dotnet test
# Schema changes need a migration for each database:
dotnet tool restore
dotnet ef migrations add <Name> -p src/PlaneWeb.Infrastructure -s src/PlaneWeb.Infrastructure --context SqlitePlaneWebDbContext -o Data/Migrations/Sqlite
dotnet ef migrations add <Name> -p src/PlaneWeb.Infrastructure -s src/PlaneWeb.Infrastructure --context PostgresPlaneWebDbContext -o Data/Migrations/Postgres
```

## Layout
```
src/PlaneWeb.Core            models, geo math, callsign normalization
src/PlaneWeb.Infrastructure  ADS-B providers, route lookup, EF Core SQLite, polling service
src/PlaneWeb.Web             Blazor UI (wall, settings), Leaflet interop
tests/PlaneWeb.Tests         xUnit tests
```
