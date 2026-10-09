# AGENTS.md

.NET 10 Blazor Server app ("The Plane Web"). See `README.md` for product behavior; this file is for working in the code.

## Commands
- Run: `PlaneWeb__Auth__AdminEmail=me@example.com PlaneWeb__Auth__AdminPassword=dev-password-1 dotnet run --project src/PlaneWeb.Web` (admin env vars are required or no login exists).
- Test all: `dotnet test`. Single test: `dotnet test --filter "FullyQualifiedName~ClassName.MethodName"`.
- CI gate: `dotnet build -c Release` then `dotnet test --no-build -c Release`. Targets `net10.0` (SDK 10.0.x).

## Database & migrations (the main gotcha)
- EF Core with **two providers**: SQLite (default, prod) and Postgres (opt-in via `PlaneWeb__Database=Postgres`). Each has its own `DbContext` subclass and its own migration folder.
- Every schema change needs a migration generated for **both**:
  ```
  dotnet tool restore
  dotnet ef migrations add <Name> -p src/PlaneWeb.Infrastructure -s src/PlaneWeb.Infrastructure --context SqlitePlaneWebDbContext   -o Data/Migrations/Sqlite
  dotnet ef migrations add <Name> -p src/PlaneWeb.Infrastructure -s src/PlaneWeb.Infrastructure --context PostgresPlaneWebDbContext -o Data/Migrations/Postgres
  ```
- Keep the schema provider-portable: no jsonb/arrays/enums/raw SQL/PostGIS. Lists (e.g. `TrackedFlights`) are stored as JSON in a plain text column via an EF value converter.
- Migrations auto-apply at startup (`PlaneWeb.Web/Program.cs`). The DB only holds Identity users/roles, per-user `Settings`, and `GoogleAllowedUsers` — not flight data.

## Architecture
- `src/PlaneWeb.Core` — models, geo math, callsign normalization (`UA123` → `UAL123`). No infra deps.
- `src/PlaneWeb.Infrastructure` — ADS-B providers, route/aircraft lookups, EF Core, hosted polling services. Everything is wired in `DependencyInjection.cs:AddPlaneWeb`.
- `src/PlaneWeb.Web` — Blazor UI (`/wall`, `/map`, `/settings`), Leaflet JS interop, auth.
- `tests/PlaneWeb.Tests` — xUnit.
- Flight data is **not** in the DB: live positions in memory, trails in `/data/trails.json`, logos cached on disk, aircraft info cached in memory.

## External data wiring (`DependencyInjection.cs`)
- Providers: adsb.lol primary, adsb.fi fallback (`FallbackFlightDataProvider`); order flips with `PlaneWeb__Provider`.
- All upstream HTTP uses `AddStandardResilienceHandler`. planespotters.net requires a contact URL in the User-Agent (`PlaneWeb__ContactUrl`). adsb.lol trace files come from the website host (not the API), may redirect, and are rate-limited — failures are expected and swallowed.
- Hosted service registration order matters: trails are restored before the first poll.

## Deploy / CI
- CI (`.github/workflows/ci.yml`) runs build+test, then Docker smoke tests on **both** SQLite and Postgres. The smoke test asserts: `/healthz`→200, `/settings`→302, `/api/aircraft/...`→401, every JS/CSS referenced by the login page is served (catches a broken Blazor runtime), and log contains `Created bootstrap admin`. Don't break these.
- Push to `main` auto-deploys to an Azure VM running this Docker Compose setup (SQLite + Caddy).
