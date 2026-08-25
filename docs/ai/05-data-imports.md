# Data imports

Runtime reads **PostGIS only**. Imports are one-time / rare; startup auto-imports if districts missing.

## DataImports files

| File | In repo | Role |
|------|---------|------|
| `Granice osiedli.csv` | Yes | Łódź address points per osiedle (~63k) — not WKT |
| `lodz-osiedla-polygons.json` | Generate | Łódź MultiPolygon cache |
| `krakow-districts-polygons.json` | Generate | Kraków districts |
| `warszawa-districts-polygons.json` | Generate | Warszawa districts |
| `wind-rose.json` | Yes | Env wind frequencies by city GUID |
| `lodz-pollution-sources.json` | Yes | Curated pollution points (`id`, `type`, `name`, `lat`, `lon`, `weight`, `influenceKm`, `notes`) |
| `_fetch_osiedla_polygons.py` | Yes | Regenerate Łódź polygons from OSM |
| `_fetch_krakow_warszawa.py` | Yes | Regenerate KR/WA polygons |

Polygon JSON files may be absent on a fresh clone — run the fetch scripts before first Łódź/KR/WA import.

Docker: `./DataImports` mounted read-only at `/app/DataImports`; also `COPY`’d into image.

Config paths (`ImportOptions` / compose):

- `Import__LodzDistrictsCsvPath` → `Granice osiedli.csv`  
- `Import__LodzDistrictsPolygonsPath` → `lodz-osiedla-polygons.json`  

## Łódź district pipeline (`LodzDistrictImportService`)

1. CSV rows → stage table `districts_import_raw`  
2. Match osiedle names to `lodz-osiedla-polygons.json`  
3. Write `District.Geom` as MultiPolygon SRID 4326  
4. Clear staging  

- **Startup:** auto if Łódź has zero districts (warn on failure)  
- **Admin:** `POST /api/admin/import/lodz-districts`  

## Kraków / Warszawa (`PolygonDistrictImportService`)

- Paths in `Program.cs`: `DataImports/krakow-districts-polygons.json`, `…/warszawa-districts-polygons.json`  
- Imports only if that city has **zero** districts  

## Regenerate polygon caches

```bash
python DataImports/_fetch_osiedla_polygons.py
python DataImports/_fetch_krakow_warszawa.py
```

(Edit hardcoded `OUT` paths in scripts if not on Windows.)

## Critical for backups / migrations

Re-importing districts creates **new** `DistrictId` GUIDs. User notes, picks, visits, offers FK to old IDs.

**Always restore Postgres dump with user data** — GeoJSON alone is not enough. See [06-ops.md](06-ops.md).

Env cache (`DistrictEnvironments`, `CityEnvironmentSources`), Otodom pin cache (`OtodomPinSets`, `OtodomPins`), and `districts_import_raw` are excluded from backup dumps (regenerable).
