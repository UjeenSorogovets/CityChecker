# Architecture

## Startup (`Program.cs`)

1. `MigrateAsync()` — all EF migrations  
2. `SeedData.EnsureSeededAsync` — upsert missing seeded cities (fixed GUIDs)  
3. Auto-import Łódź districts if `Districts` empty for Łódź  
4. Auto-import Kraków/Warszawa/Wrocław/Gdańsk from polygon JSON if each city has zero districts  

**Development only:** `GeoHelper.SelfCheck()`, `PasswordAuth.SelfCheck()`.

**Production:** `UseForwardedHeaders()` → HSTS → HTTPS redirect → middleware redirects bare-IP host to `App:PublicBaseUrl`.

## EF migrations (order)

| Migration | Adds |
|-----------|------|
| `InitialPostGis` | Cities, districts, buildings, notes, PostGIS |
| `AddUsers` | Email/password accounts |
| `AddHousingDecision` | Anchors, picks, visits, offers, decision profile |
| `PointNotesReplaceDistrict` | Point lat/lon/radius; deletes old district-level notes |
| `AddDistrictEnvironment` | `DistrictEnvironments`, `CityEnvironmentSources` |
| `AddOtodomPinCache` | `OtodomPinSets`, `OtodomPins` (shared Otodom overlay cache) |

## Auth

| Scheme | Token | Validation |
|--------|-------|------------|
| `PasswordAuth.Scheme` | HMAC JWT from register/login | `PasswordAuth.ValidationParameters` |
| `Google` | Google ID token (frontend stores as Bearer) | `accounts.google.com`, audience = `Google:ClientId` |

- Policy scheme `"Bearer"` forwards to Google vs local based on JWT payload  
- `MapInboundClaims = false` — use `GetUserId()` / `sub`, not `ClaimTypes`  
- **Public:** `GET /api/config`, `POST /api/auth/register`, `POST /api/auth/login`  
- **All other `/api/*`:** `Authorization: Bearer <jwt>`  

`Google:AllowedUserId` exists in config/docker-compose for documentation/hints; **not enforced** by `EnsureOwner`.

## Data model (key tables)

| Table | Role |
|-------|------|
| `Cities` | Seeded (Łódź, Kraków, Warszawa, Wrocław, Gdańsk) |
| `Districts` | Import polygons; **IDs unstable across re-import** |
| `Buildings` | Reverse-geocode cache / user-created |
| `Notes` | User content; Point resolves `TargetDistrictId` via `Geom.Contains` |
| `Users` | Local accounts (`PasswordHash` PBKDF2) |
| `MapAnchors`, `DistrictPicks`, `DistrictVisits`, `HousingOffers`, `DecisionProfiles` | Decide / housing |
| `DistrictEnvironments`, `CityEnvironmentSources` | Environment cache (~7 days, regenerable) |
| `OtodomPinSets`, `OtodomPins` | Shared Otodom overlay cache (filter-keyed; Refresh scrapes) |
| `districts_import_raw` | Transient Łódź CSV staging |

**Offers access:** `Offers:AllowedEmails` (comma-separated, case-insensitive). Empty = deny all. Checked on `/api/housing/offers*`, `/api/housing/otodom/*`. JWT may carry `email` claim (login/register/Google); password users also resolved via `Users` table.  
**Update offers:** `Offers:IsUpdateOffers` (default `false`) — UI + `/otodom/pins/refresh` gated separately from view access.

## API map

| Area | File | Routes |
|------|------|--------|
| Config | `Program.cs` | `GET /api/config` |
| Auth | `AuthEndpoints.cs` | `POST /api/auth/register`, `POST /api/auth/login` |
| Cities | `CityEndpoints.cs` | `GET /api/cities`, `GET /api/cities/{id}`, `GET /api/cities/{id}/environment` |
| Districts | `CityEndpoints.cs` (`DistrictEndpoints`) | `GET /api/cities/{cityId}/districts`, `…/geojson`, `GET /api/districts/{id}` |
| Buildings | `BuildingEndpoints.cs` | `GET /api/cities/{cityId}/buildings?bbox=…`, `GET …/building-footprints` (Wołomin OSM pilot), `POST /api/buildings/reverse-geocode` |
| Notes | `NoteEndpoints.cs` | `GET/POST/PUT/DELETE /api/notes` (+ filters) |
| Aggregates | `AggregateEndpoints.cs` | `/api/aggregates/city|district|building/{id}`, **`GET /api/cities/{cityId}/aggregates`** (batch) |
| Housing | `HousingEndpoints.cs` | `/api/housing/*` — anchors, commute, picks, probe, visits, offers, **`POST /otodom/pins`**, **`POST /otodom/pins/refresh`**, profile, compare, finalists, `export.csv` |
| Admin | `AdminEndpoints.cs` | `POST /api/admin/import/lodz-districts`, `POST /api/admin/refresh-environment/{cityId}` |

## Services

| Service | Registration | Role |
|---------|--------------|------|
| `AggregateService` | Scoped | Note score averages (incl. batch for map) |
| `BuildingService` | Scoped | Nominatim + district contain |
| `LodzDistrictImportService` | Scoped | CSV + polygons → `Districts` |
| `PolygonDistrictImportService` | Scoped | KR/WA GeoJSON → `Districts` |
| `EnvironmentService` | `AddHttpClient<>` (scoped) | Overpass + wind + curated JSON → env cache |
| `OtodomMapService` | `AddHttpClient<>` + memory cache (buildId/coords) | Shared DB pin sets; Refresh scrapes Otodom Next.js data |
| `HousingGeoService` | HttpClient | OSRM commute + Overpass amenity probe |
| `NominatimClient` | HttpClient | Reverse geocode |

## Configuration

| Source | Notes |
|--------|-------|
| `appsettings.json` | DB, Google, Nominatim, Import paths; `AllowedHosts: *`; CORS `:8080`, `:5097` |
| `appsettings.Production.json` | `AllowedHosts` ujeen.pl; HTTPS CORS |
| `.env` / compose | `AUTH_JWT_SECRET`, `GOOGLE_*`, `DOMAIN`, `APP_PUBLIC_BASE_URL`, `CONTACT_EMAIL`, optional `SSH_*` |
| Local compose | `Development`, API **8080** published, `App__PublicBaseUrl=http://localhost:8080` |
| Prod compose | `Production`, Caddy **80/443**, API internal only |

Local DB (host): `localhost:5432`, user/pass/db `citychecker`.

## SPA fallback

`MapFallbackToFile("index.html")` — all non-API routes serve the SPA.
