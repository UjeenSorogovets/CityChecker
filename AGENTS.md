# AGENTS.md — AI context for CityChecker

This file is for AI coding assistants. Humans: see [README.md](README.md) for commands only.

## What this project is

**Poland City Comfort Mapper** — a personal tool to score and annotate Polish locations at three granularities:

| Level | Enum | How it works |
|-------|------|--------------|
| City | `NoteLevel.City` | Whole-city notes |
| Point | `NoteLevel.Point` | Map point with `lat`, `lon`, `radiusMeters` (default 300, clamp 50–2000). District fill = average of points inside polygon via `ST_Contains` on write |
| Building | `NoteLevel.Building` | Tied to a `Building` entity (reverse-geocoded address) |

**Housing decision tools** (Decide panel): anchors, district shortlist/veto, OSRM commute compare, Overpass amenity probe, visit checklists, rent/buy offers, finalists matrix, ranking weights.

**Auth:** email/password (JWT, `PasswordAuth.Scheme`) + optional Google Sign-In (JWT, `Google` scheme). Single-user personal app — any authenticated user is allowed (`EnsureOwner` only checks signed-in). Google OAuth needs HTTPS + domain; password auth works on plain `http://IP:8080`.

**Deploy:** VPS at `/opt/CityChecker`. Production: `docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d` → **https://ujeen.pl** (Caddy 80/443 → api:8080 internal). Local dev: `docker compose up` → localhost:8080. User commits/pushes themselves — do not commit unless asked.

## Repository layout

```
CityChecker/
├── src/CityChecker.Api/          # Single project: API + SPA (wwwroot)
│   ├── Program.cs                # DI, auth, migrations, startup imports
│   ├── Endpoints/                # Minimal API route groups
│   ├── Services/                 # Aggregates, buildings, housing geo, imports
│   ├── Data/                     # EF Core, entities, migrations, SeedData
│   ├── Auth/                     # PasswordAuth, AuthExtensions
│   ├── Dtos/Dtos.cs
│   └── wwwroot/                  # Leaflet SPA (no bundler)
│       ├── index.html
│       ├── css/app.css
│       └── js/
│           ├── app.js            # Map, notes, city lock, FAB place-note
│           ├── housing.js        # Decide panel
│           ├── api.js            # fetch + JWT in localStorage
│           └── i18n.js           # EN/RU
├── DataImports/                  # CSV + polygon JSON caches (mounted ro in Docker)
├── docker-compose.yml
├── docker-compose.prod.yml       # Caddy TLS overlay
├── Caddyfile.prod
├── Dockerfile
├── .env.example
├── README.md                     # Human: commands only
└── AGENTS.md                     # This file
```

## Tech stack

- **.NET 10** minimal API, no controllers
- **EF Core + Npgsql + NetTopologySuite** — PostGIS `geometry` on `District.Geom`
- **PostgreSQL 16** via `postgis/postgis:16-3.4` image
- **Frontend:** vanilla ES modules, Leaflet 1.9.4 from CDN, no npm/webpack
- **External APIs:** Nominatim (reverse geocode), OSRM (commute), Overpass (amenities)

## Startup sequence (`Program.cs`)

1. `MigrateAsync()` — applies EF migrations
2. `SeedData.EnsureSeededAsync` — cities (Łódź, Kraków, Warszawa, …)
3. Auto-import Łódź districts from CSV+polygons if `Districts` empty for Łódź
4. Auto-import Kraków/Warszawa from polygon JSON if zero districts

## Data model (key entities)

- **City** — `CenterLat/Lon`, `OfficialCode`, seeded GUIDs in `SeedData`
- **District** — `Geom` (MultiPolygon 4326), belongs to City
- **Building** — address + lat/lon, optional `DistrictId`
- **Note** — `Level`, `TargetCityId`, optional `TargetDistrictId`/`TargetBuildingId`, `Lat`/`Lon`/`RadiusMeters` for Point
- **AppUser** — email + password hash for local auth
- **Housing:** `Anchor`, `DistrictPick`, `DistrictVisit`, `HousingOffer`, `DecisionProfile`

`AuthorGoogleId` on notes stores any user id (`sub` claim) — name is legacy.

## API surface

Public (no auth): `GET /api/config`, `POST /api/auth/register`, `POST /api/auth/login`

All other `/api/*` requires `Authorization: Bearer <jwt>`.

| Group | File | Main routes |
|-------|------|-------------|
| Auth | `AuthEndpoints.cs` | register, login |
| Cities | `CityEndpoints.cs` | list, aggregates batch, districts geojson, buildings bbox |
| Districts | (in CityEndpoints or separate) | detail |
| Buildings | `BuildingEndpoints.cs` | reverse-geocode |
| Notes | `NoteEndpoints.cs` | CRUD; Point resolves district via `Geom.Contains` |
| Aggregates | `AggregateEndpoints.cs` | city/district/building averages |
| Housing | `HousingEndpoints.cs` | anchors, picks, visits, offers, compare, weights |
| Admin | `AdminEndpoints.cs` | `POST /api/admin/import/lodz-districts` |

JSON enums are serialized as strings (`JsonStringEnumConverter`).

## Frontend architecture (`wwwroot/js/app.js`)

### City lock

- User picks one city at a time; persisted in `localStorage` key `cc_city_id`
- `lockedCityId` — working city; `minZoom` 11 prevents zooming out to Poland overview
- City picker overlay on first visit; left-edge drawer to switch

### Zoom modes

| Zoom | Mode | Behavior |
|------|------|----------|
| ≤ 10 | city | (unlocked) pick city markers |
| 11–14 | district | district polygons colored by point-note averages |
| ≥ 15 | building | building markers in viewport |

### Map interaction (mobile-first)

- **Tap** point center dot → select note (sheet only, no auto-open form)
- **Tap** district polygon → select district (avg + point list + housing actions)
- **Tap** building marker → select building
- **Tap** empty map → clear selection, city-level sheet
- **Drag FAB** (`#place-note-fab`, bottom-right) onto map → create new point note (opens form)
- Influence circles (`L.circle`) are `interactive: false`; only center dots receive clicks
- Shift+click empty map at building zoom → reverse-geocode building (desktop shortcut)

### Key JS globals / layers

- `cityLayer`, `districtLayer` (GeoJSON), `buildingLayer`, `pointLayer`
- `context` — current sheet target `{ level, cityId, districtId?, buildingId?, lat?, lon? }`
- `districtScores` / `buildingScores` — from batch aggregates endpoint

### i18n

`i18n.js` — `t(key)`, `toggleLang()`, `data-i18n` attributes in HTML. Always add EN + RU for new UI strings.

## Migrations

Create from `src/CityChecker.Api`:

```bash
dotnet ef migrations add MigrationName --project src/CityChecker.Api
```

Migrations live in `Data/Migrations/`. Applied automatically on startup.

Notable: `PointNotesReplaceDistrict` deletes old `Level=1` district notes and adds `Lat`/`Lon`/`RadiusMeters`.

## Configuration

| Source | Keys |
|--------|------|
| `appsettings.json` | ConnectionStrings, Google, Nominatim, Import paths |
| `.env` / docker-compose | `GOOGLE_CLIENT_ID`, `GOOGLE_ALLOWED_USER_ID`, `AUTH_JWT_SECRET`, `CONTACT_EMAIL`, `DOMAIN`, `ACME_EMAIL` |
| Docker api service | `ASPNETCORE_ENVIRONMENT: Production`, connection to `db` host |
| Production overlay | `App__PublicBaseUrl`, `Cors__AllowedOrigins__*`, no public `api` ports |
| `appsettings.Production.json` | `AllowedHosts`: ujeen.pl;www.ujeen.pl, `App:PublicBaseUrl` |

Local dev DB: `localhost:5432`, user/pass/db `citychecker`.

## District import pipeline (Łódź)

1. CSV `Granice osiedli.csv` — address points per osiedle (~63k rows), **not** WKT
2. Stage to `districts_import_raw`, match names to `lodz-osiedla-polygons.json`
3. Write `District.Geom` as MultiPolygon 4326
4. One-time/rare; day-to-day app reads PostGIS only

Regenerate polygon caches: `python DataImports/_fetch_osiedla_polygons.py`, `python DataImports/_fetch_krakow_warszawa.py`.

## Coding conventions (for AI)

- **Lazy senior / minimal diff** — reuse existing patterns; no new dependencies unless necessary
- **English only** in code, comments, commits, and user-facing strings (i18n has RU translations)
- **No commits/push** unless user explicitly asks
- Endpoints: static classes with `Map*Endpoints(this WebApplication app)`, `RequireAuthorization()` on groups
- Owner check: `user.EnsureOwner(config)` at start of handlers
- SPA: ES modules, no build step — edit files in `wwwroot` directly
- CSS: mobile-first; bottom sheet on phone, side panel ≥900px
- After note save/delete for points: call `reloadDistrictColors()` + `loadPointNotes()`

## Common tasks

### Add API endpoint

1. Add DTO in `Dtos.cs` if needed
2. Add handler in `Endpoints/*.cs`
3. Register in `Program.cs` via `app.Map*Endpoints()`
4. Wire frontend in `api.js` / relevant JS module

### Add DB column

1. Update entity in `Data/Entities/`
2. `dotnet ef migrations add ...`
3. Update DTOs and endpoints
4. Migration runs on next startup

### Change map behavior

Primary file: `wwwroot/js/app.js`. Housing-specific: `housing.js`. Styles: `app.css`.

### Verify locally

```bash
docker compose up db -d
dotnet run --project src/CityChecker.Api
# or full stack:
docker compose up --build -d
```

## Known gotchas

- **Google on IP/HTTP fails** — use email/password or HTTPS domain
- **Do not `fitBounds` on district load** — caused mobile zoom loop; city `setView` first
- **`setMinZoom(11)` before centering on city** — otherwise map clamps to wrong place
- **JWT claim mapping** — `MapInboundClaims = false`; use `GetUserId()` not `ClaimTypes`
- **District enum value 1** is now `Point`; old district notes were deleted in migration
- **Docker Production** — `ASPNETCORE_ENVIRONMENT: Production` in compose; Development enables `GeoHelper.SelfCheck`
- **Leaflet click vs touch** — FAB uses pointer events; map click creates nothing when city locked (FAB only for new points)

## Verify checklist (manual)

- Sign up / sign in on `http://localhost:8080`
- Pick city → districts load, point FAB visible
- Drag FAB → new point note, district fill updates
- Tap existing point → sheet, Edit opens form
- Tap district → housing actions, no new note
- Tap empty map → city sheet
- Building zoom → building markers, tap selects
- Decide panel: anchor, shortlist, compare refresh
