# Environment risk layer

Pollution / odor / industrial proximity overlay on the main map (toggle **Environment** in topbar).

## Backend (`EnvironmentService.cs`)

Registered via `AddHttpClient<EnvironmentService>` (90s timeout).

### Pipeline

1. **Overpass** bbox query around district centroids (+0.15° pad)  
   - Primary: `overpass.openstreetmap.fr`  
   - Fallback: `overpass-api.de` (may 406)  
2. **Wind rose** — `DataImports/wind-rose.json` keyed by city GUID  
3. **Curated Łódź sources** — `DataImports/lodz-pollution-sources.json` (only `SeedData.LodzId`)  
4. Score each district centroid; persist cache + sources GeoJSON  

### Cache

- Tables: `DistrictEnvironments` (per district), `CityEnvironmentSources` (FeatureCollection JSON)  
- TTL: **7 days** (`CacheTtl`)  
- On compute failure: return **stale cache** if any (`ignoreTtl: true`), else empty DTO  
- **Refresh:** `POST /api/admin/refresh-environment/{cityId}` or first fetch after TTL expiry  

### Overpass → feature types

| type | Map rings | Scoring |
|------|-----------|---------|
| `landfill` | Named OSM + curated; unnamed OSM → marker only (`influenceKm: 0`) | Odor family |
| `waste_incinerator` | Yes | Odor family (higher base) |
| `waste_transfer` | Yes | Odor family |
| `factory` / `power_plant` | OSM → marker only; curated → ring | Industrial distance |
| `airport` | Marker, no ring | Distance only |
| `rail` | **Not shown** (`influenceKm: 0`) | Distance only |
| highways | Not as point features | Nearest motorway/trunk/primary |

### Wind / wedges

- Wind rose = frequency wind comes **FROM** each compass sector (meteorological)  
- `PrevailingWindFromBearing` → dominant sector  
- Feature properties:  
  - `windFromBearing` / `windFrom` — meteorological FROM  
  - `windBearing` / `windTo` — **downwind plume** (`windFrom + 180°`)  
- District `landfillDownwind`: nearest landfill exists AND wind sector aligned with source→district bearing at **`DownwindFreqThreshold` (0.14)**  
- Downwind adds +2 to odor risk (cap 10)  

### Scoring components (1–10 each, overall = max)

| Component | Function |
|-----------|----------|
| Landfill | `LandfillRisk(km, downwind)` |
| Incinerator | `IncineratorRisk(km, downwind)` |
| Rail | `RailRisk(km)` |
| Airport | `AirportRisk(km)` |
| Industrial | `IndustrialRisk(km)` |
| Highway | `HighwayRisk(km)` |

Source GeoJSON properties include: `type`, `name`, `weight`, `influenceKm`, `showRing`, `curated`, `notes`, wind labels.

## API

```
GET  /api/cities/{cityId}/environment
POST /api/admin/refresh-environment/{cityId}
```

`CityEnvironmentDto`: `{ computedAt, districts[], sources }`  
`DistrictEnvironmentDto`: `districtId`, `envRiskOverall`, `nearestLandfillKm`, `landfillDownwind`, `nearestRailKm`, `nearestAirportKm`, `nearestIndustrialKm`, `nearestHighwayKm`

## Frontend (`app.js`)

- `loadEnvironment(cityId)` — gen counter; never shares `mapAbort`  
- `loadRiskSources(sources)` — up to **20** ring features (curated/weight sorted); pane `"risk"` z-index **650**  
- Ring: dashed `L.circle`; wedge: `L.polygon` 38° half-angle, bearing = `windBearing` (downwind)  
- Markers: circleMarkers for landfill/incinerator/transfer/airport (+ curated factories with rings)  
- Visible when: Environment mode + locked city + district zoom (`currentMode`)  
- Toggle to Environment re-triggers load if prior fetch was aborted  

## After changes

1. Rebuild API container if backend/wwwroot changed  
2. Bump `app.js?v=` in `index.html` when fighting browser cache  
3. Force env refresh if scoring/source logic changed  
4. Debug screenshot: `python tools/shot_env.py` → `screenshots/lodz-environment.png`  

Backup excludes env cache — refresh after restore. See [../backup-restore.md](../backup-restore.md).
