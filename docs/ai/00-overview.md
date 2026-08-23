# Overview

**Poland City Comfort Mapper** — personal tool to score and annotate Polish locations.

## Note levels

| Level | Enum | Value | Behavior |
|-------|------|-------|----------|
| City | `NoteLevel.City` | 0 | Whole-city notes |
| Point | `NoteLevel.Point` | 1 | Map point + `lat`/`lon`/`radiusMeters` (default 300, clamp 50–2000). District fill = average of points inside polygon (`ST_Contains` on write) |
| Building | `NoteLevel.Building` | 2 | Tied to reverse-geocoded `Building` |

Whole-district notes were removed (`PointNotesReplaceDistrict` migration). District comfort scores come from point notes inside the polygon.

## Major feature areas

1. **Comfort map** — notes, district coloring by note averages, buildings at high zoom  
2. **Environment map** — pollution/odor/industrial risk overlay (Comfort / Environment toggle)  
3. **Decide panel** (`housing.js`) — anchors, shortlist/veto, OSRM commute, Overpass amenity probe, visits, offers, finalists matrix, ranking weights  

## Auth model

- **Email/password** → HMAC JWT (`PasswordAuth.Scheme`)  
- **Google Sign-In** → Google ID token sent as `Authorization: Bearer` (`Google` scheme); backend picks scheme from JWT payload  
- Single-user personal app: `EnsureOwner` only checks signed-in (`GetUserId()`), not a whitelist  
- Google needs HTTPS + domain; password auth works on `http://localhost:8080`  
- Notes store author in `AuthorGoogleId` (any auth `sub` — legacy column name)  
- Edit/delete notes: must match `AuthorGoogleId`  

Frontend token: `sessionStorage` key `cc_id_token`. Client-side expiry check in `api.js` (`isTokenExpired`).

## Deploy targets

| Env | URL | Compose |
|-----|-----|---------|
| Local | http://localhost:8080 | `docker compose up` or `.\run.ps1` / `./run.sh` (`Development`) |
| Prod | https://ujeen.pl | `docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d` |

VPS path: `/opt/CityChecker`. Do **not** commit/push unless the user asks.

## Seeded city GUIDs

| City | GUID | Official code |
|------|------|---------------|
| Łódź | `11111111-1111-1111-1111-111111111111` | 1061 |
| Kraków | `22222222-2222-2222-2222-222222222222` | 1261 |
| Warszawa | `33333333-3333-3333-3333-333333333333` | 1465 |

District GUIDs are **random per import** — never stable across re-import. Backups must include `Districts` with user FK data.

## Tech stack

- **.NET 10** minimal API (no controllers)  
- **EF Core + Npgsql + NetTopologySuite** — PostGIS `geometry` on `District.Geom`  
- **PostgreSQL 16** — `postgis/postgis:16-3.4`  
- **Frontend** — vanilla ES modules, Leaflet 1.9.4 CDN, no npm/webpack  
- **External** — Nominatim (reverse geocode), OSRM (commute), Overpass (amenities + environment)  

## Repository layout

```
CityChecker/
├── src/CityChecker.Api/       # API + SPA (wwwroot)
│   ├── Program.cs             # DI, auth, migrations, startup imports
│   ├── Endpoints/             # Minimal API route groups
│   ├── Services/              # Aggregates, buildings, housing geo, imports, environment
│   ├── Data/                  # EF Core, entities, migrations, SeedData
│   ├── Auth/                  # PasswordAuth, AuthExtensions
│   ├── Dtos/Dtos.cs
│   └── wwwroot/               # index.html, css/, js/
├── DataImports/               # CSV + polygon JSON + wind/pollution (Docker mount ro)
├── scripts/                   # backup/restore, remote.py (SSH probe)
├── tools/
│   ├── citychecker_mcp/       # Cursor MCP server (stdio → local API)
│   └── shot_env.py            # Playwright screenshot helper (Environment mode)
├── backups/                   # gitignored archives
├── docs/                      # Human ops
├── docs/ai/                   # This folder
├── .cursor/
│   ├── rules/                 # citychecker-ai-context.mdc
│   └── mcp.json.example       # Copy → mcp.json (gitignored)
├── docker-compose.yml
├── docker-compose.prod.yml
├── Caddyfile.prod
├── run.ps1 / run.sh             # Local foreground compose
├── run-prod.ps1 / run-prod.sh   # Prod detached compose
├── AGENTS.md                    # Short AI pointer
└── README.md                    # Short human commands
```
