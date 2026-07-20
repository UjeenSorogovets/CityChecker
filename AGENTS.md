# AGENTS.md — AI context for CityChecker

This file is for AI coding assistants. Humans: see [README.md](README.md) for commands only.

## What this project is

**Poland City Comfort Mapper** — a personal tool to score and annotate Polish locations at three granularities:

| Level | Enum | How it works |
|-------|------|--------------|
| City | `NoteLevel.City` | Whole-city notes |
| Point | `NoteLevel.Point` | Map point with `lat`, `lon`, `radiusMeters` (default 300, clamp 50–2000). District fill = average of points inside polygon via `ST_Contains` on write |
| Building | `NoteLevel.Building` | Tied to a `Building` entity (reverse-geocoded address) |

Whole-district notes were removed; district scores come from point notes inside the polygon.

**Housing decision tools** (Decide panel): anchors, district shortlist/veto, OSRM commute compare, Overpass amenity probe, visit checklists, rent/buy offers, finalists matrix, ranking weights.

**Auth:** email/password (JWT, `PasswordAuth.Scheme`) + optional Google Sign-In (JWT, `Google` scheme). Single-user personal app — any authenticated user is allowed (`EnsureOwner` only checks signed-in). Google OAuth needs HTTPS + domain; password auth works on plain `http://localhost:8080` or `http://IP:8080`.

**Deploy:** VPS at `/opt/CityChecker`, SSH `root@ujeen.pl` (or server IP). Production: `docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d` → **https://ujeen.pl** (Caddy 80/443 → api:8080 internal). Local: `docker compose up` → **http://localhost:8080** (`Development` env). User commits/pushes themselves — do not commit unless asked.

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
│           ├── app.js            # Map, notes, city lock, sheet snap, FAB
│           ├── housing.js        # Decide panel
│           ├── api.js            # fetch + JWT in localStorage
│           └── i18n.js           # EN/RU
├── DataImports/                  # CSV + polygon JSON caches (mounted ro in Docker)
├── docker-compose.yml            # Local: api:8080 published, ASPNETCORE_ENVIRONMENT=Development
├── docker-compose.prod.yml       # Prod overlay: Caddy, no public api port, Production env
├── Caddyfile.prod
├── Dockerfile
├── run.ps1 / run.sh              # Local foreground compose
├── run-prod.ps1 / run-prod.sh    # Prod detached compose
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

In **Development** only: `GeoHelper.SelfCheck()`, `PasswordAuth.SelfCheck()`.

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
| Cities / districts | `CityEndpoints.cs` | cities list, districts geojson, buildings bbox, district detail (`MapDistrictEndpoints`) |
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
- **Tap** empty map → clear selection, city-level sheet, sheet snap **peek**
- **Drag FAB** (`#place-note-fab`) onto map → create new point note (opens form)
- Drop uses `map.mouseEventToLatLng(e)` + hit test inside map container (not manual rect math)
- Influence circles (`L.circle`) are `interactive: false`; only center dots receive clicks
- Shift+click empty map at building zoom → reverse-geocode building (desktop shortcut)

### Mobile bottom sheet (≤899px)

HTML: `.sheet-chrome` (handle, title, meta — always visible) + `.sheet-body` (scrollable notes).

Snap classes via `setSheetSnap("peek" | "half" | "full")`:

| Snap | max-height | When |
|------|------------|------|
| `sheet-peek` | 5.5rem | Clear selection / empty map tap |
| `sheet-half` | 45dvh | Default; district/point/building select; after save |
| `sheet-full` | 78dvh | Note list length > 2 |

Handle: tap cycles half → full → peek; drag up/down snaps (40px threshold). Desktop (≥900px): side panel, snap classes ignored, handle hidden.

### FAB (`#place-note-fab`)

- Sibling of `#sheet` inside `#app`, `z-index: 950`
- Mobile: `updateFabPosition()` sets `bottom` from sheet height + 12px + safe-area (ResizeObserver on `#sheet`, window resize, snap changes)
- Hidden while `#note-dialog` is open
- Desktop: fixed bottom-right above map area (CSS), not dynamic

### Key JS globals / layers

- `cityLayer`, `districtLayer` (GeoJSON), `buildingLayer`, `pointLayer`
- `context` — current sheet target `{ level, cityId, districtId?, buildingId?, lat?, lon? }`
- `districtScores` / `buildingScores` — from batch aggregates endpoint

### i18n

`i18n.js` — `t(key)`, `toggleLang()`, `data-i18n` / `data-i18n-aria` / `data-i18n-title` in HTML. Always add EN + RU for new UI strings.

## Migrations

Create from repo root:

```bash
dotnet ef migrations add MigrationName --project src/CityChecker.Api
```

Migrations live in `Data/Migrations/`. Applied automatically on startup.

Notable: `PointNotesReplaceDistrict` deletes old district-level notes and adds `Lat`/`Lon`/`RadiusMeters`.

## Configuration

| Source | Keys / behavior |
|--------|-----------------|
| `appsettings.json` | ConnectionStrings, Google, Nominatim, Import paths; `AllowedHosts: *`; local CORS includes `:8080` and `:5097` |
| `appsettings.Production.json` | `AllowedHosts`: ujeen.pl;www.ujeen.pl; HTTPS CORS origins |
| `.env` / docker-compose | `GOOGLE_CLIENT_ID`, `GOOGLE_ALLOWED_USER_ID`, `AUTH_JWT_SECRET`, `CONTACT_EMAIL`, `DOMAIN`, `APP_PUBLIC_BASE_URL` |
| `docker-compose.yml` (local) | `ASPNETCORE_ENVIRONMENT=Development`, `App__PublicBaseUrl=http://localhost:8080`, api port **8080** published |
| `docker-compose.prod.yml` | `ASPNETCORE_ENVIRONMENT=Production`, Caddy **80/443**, api **8080** internal only, host Certbot certs at `/etc/letsencrypt` |

Local dev DB: `localhost:5432`, user/pass/db `citychecker`.

Production redirects IP host requests to `App:PublicBaseUrl` (middleware in `Program.cs`).

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
docker compose up --build -d
curl -sI http://127.0.0.1:8080/
# or API on host:
docker compose up db -d
dotnet run --project src/CityChecker.Api
```

## Known gotchas

- **Invalid Hostname on localhost** — local compose must use `Development`; prod `AllowedHosts` rejects `localhost` (HTTP 400)
- **Google on IP/HTTP fails** — use email/password or HTTPS domain
- **Do not `fitBounds` on district load** — caused mobile zoom loop; city `setView` first
- **`setMinZoom(11)` before centering on city** — otherwise map clamps to wrong place
- **JWT claim mapping** — `MapInboundClaims = false`; use `GetUserId()` not `ClaimTypes`
- **District enum value 1** is `Point`; old district notes were deleted in migration
- **FAB drop offset on mobile** — use `map.mouseEventToLatLng`, not manual `getBoundingClientRect` + `containerPointToLatLng`
- **Leaflet click vs touch** — FAB uses pointer events; map click creates nothing when city locked (FAB only for new points)

## Verify checklist (manual)

- Sign up / sign in on `http://localhost:8080`
- Pick city → districts load, point FAB visible above sheet
- Drag FAB → note under finger on map, district fill updates
- Tap existing point → sheet half, Edit opens form; FAB hidden while dialog open
- Tap district with many notes → sheet full; scroll notes → handle stays visible
- Tap handle → collapse to peek (map visible)
- Tap empty map → city sheet peek
- Building zoom → building markers, tap selects
- Decide panel: anchor, shortlist, compare refresh
- Desktop ≥900px: side panel, FAB bottom-right, layout unchanged
