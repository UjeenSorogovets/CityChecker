# Poland City Comfort Mapper

Personal map tool for evaluating Polish cities / neighborhoods / buildings (notes + scores).  
ASP.NET Core minimal API + Leaflet SPA + **PostgreSQL/PostGIS**.

## What changed for Łódź districts

The portal file [`Granice osiedli.csv`](https://otwarte.miasto.lodz.pl/dane-przestrzenne/) is **not WKT/GeoJSON**.  
It is a list of ~63k street-address points grouped into **36 osiedla** (neighborhoods).

Import is a **one-time** (or rare re-run) operation. Day-to-day the app only reads PostGIS `Districts`.

Pipeline when you re-import:

1. Stage CSV rows into `districts_import_raw` (then truncate after success is fine).
2. Match unique osiedle names to `DataImports/lodz-osiedla-polygons.json`.
3. Write PostGIS `geometry(MultiPolygon, 4326)`.

Optional: regenerate OSM polygon cache with `python DataImports/_fetch_osiedla_polygons.py`.

## Quick start (Docker + PostGIS)

1. Copy `.env.example` → `.env` and set Google Client ID + your Google `sub`.
2. Ensure `DataImports/Granice osiedli.csv` and `DataImports/lodz-osiedla-polygons.json` exist (CSV is downloaded from the open-data portal; polygons are already generated).
3. **Recreate DB volume** (required once when switching to PostGIS):

```bash
docker-compose down -v
docker-compose up --build
```

4. Open http://localhost:8080 — sign in. On first boot the API auto-imports 36 Łódź osiedla when `Districts` is empty.

Re-run import manually (after sign-in, with Bearer token):

```http
POST /api/admin/import/lodz-districts
Authorization: Bearer <google-id-token>
```

## Local development

```bash
docker-compose up db -d
# connection string already in appsettings.json
dotnet run --project src/CityChecker.Api
```

Authorized JS origin: `http://localhost:5097` (and/or `http://localhost:8080` for Docker).

## API (auth required except `/api/config`)

| Method | Path | Notes |
|--------|------|--------|
| GET | `/api/cities` | City list |
| GET | `/api/cities/{id}/districts` | District metadata (no geometry) |
| GET | `/api/cities/{id}/aggregates` | Batch city + all district/building averages |
| GET | `/api/cities/{id}/districts/geojson` | Leaflet-ready FeatureCollection |
| GET | `/api/districts/{id}` | District details |
| POST | `/api/admin/import/lodz-districts` | Re-import CSV + polygons |
| CRUD | `/api/notes` | Notes |
| GET | `/api/aggregates/...` | Score averages |
| POST | `/api/buildings/reverse-geocode` | Building from lat/lon |

## Verify PostGIS (psql)

```bash
docker-compose exec db psql -U citychecker -d citychecker -c "SELECT COUNT(*) FROM \"Districts\";"
docker-compose exec db psql -U citychecker -d citychecker -c "SELECT Find_SRID('public','Districts','Geom');"
docker-compose exec db psql -U citychecker -d citychecker -c "SELECT COUNT(*) FROM \"Districts\" WHERE NOT ST_IsValid(\"Geom\");"
```

## Zoom behaviour

| Zoom | Mode |
|------|------|
| ≤ 10 | City |
| 11–14 | District / osiedle polygons |
| ≥ 15 | Building (map click → reverse geocode) |

City click zooms to band 12 so districts load. Cities without imported districts are hidden on the map.

## Production (HTTPS)

```bash
export DOMAIN=your.domain.com
export ACME_EMAIL=you@example.com
docker-compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
```

Add `https://your.domain.com` to Google OAuth Authorized JavaScript origins. Caddy terminates TLS on 80/443 and proxies to the API.

## Kraków / Warszawa

OSM dzielnice caches: `DataImports/krakow-districts-polygons.json`, `warszawa-districts-polygons.json` (18 each). Imported automatically on startup when that city has zero districts. Regenerate with `python DataImports/_fetch_krakow_warszawa.py`.

## Files

- `DataImports/Granice osiedli.csv` — Łódź open data (address lists per osiedle)
- `DataImports/lodz-osiedla-polygons.json` — Łódź OSM osiedla
- `DataImports/krakow-districts-polygons.json` / `warszawa-districts-polygons.json`
- `src/CityChecker.Api` — API + `wwwroot` SPA
- `docker-compose.prod.yml` + `Caddyfile.prod` — TLS overlay
