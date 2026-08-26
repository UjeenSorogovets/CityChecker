# Frontend

SPA: `src/CityChecker.Api/wwwroot/` — no bundler. Entry: `index.html` loads `app.js?v=otodom1` as ES module. Map rotation: **leaflet-rotate** 0.2.8 (CDN).

| File | Role |
|------|------|
| `js/app.js` | Map, auth gate, city lock, notes, sheet, FAB, Environment mode |
| `js/housing.js` | Offers panel (Otodom overlay + saved offers); `openOfferAt` |
| `js/api.js` | `fetch` wrapper, JWT in **sessionStorage** (`cc_id_token`), expiry check |
| `js/i18n.js` | EN/RU; `cc_lang` in localStorage |
| `js/voice-input.js` | Russian Web Speech API for note textarea |
| `css/app.css` | Mobile-first layout |
| `index.html` | Auth gate, map chrome, env legend, dialogs |

## Auth gate

- Tabs: sign in / sign up → `POST /api/auth/login` or `register`  
- Form wired in `initAuth` **before** Google GIS (prevents GET submit / URL leak)  
- `scrubLeakedAuthQuery()` removes stale `?email=` / `?password=`  
- Google: GIS button when `/api/config` returns real `googleClientId`; credential stored via `setToken()`  
- `sessionStorage cc_had_token` — show “session expired” if token cleared  
- `requireAuthOrGate()` / `api()` redirect to gate on 401  

## City lock

- One city: `localStorage` `cc_city_id`  
- Last map view: `localStorage` `cc_map_view` = `{ cityId, lat, lon, zoom }` — restored on re-enter / login (same browser); saved debounced on move/zoom while locked  
- `lockedCityId`; after centering call `setMinZoom(11)` (`LOCKED_MIN_ZOOM`)  
- First visit: `#city-picker` overlay; later: left-edge `#city-drawer`  

## Zoom modes (`currentMode`)

Constants: `ZOOM_CITY=10`, `ZOOM_DISTRICT=14`, `ZOOM_INTO_DISTRICT=12`.

| Zoom | Mode | Behavior |
|------|------|----------|
| ≤ 10 | city | (unlocked) city markers on Poland view |
| 11–14 | district | district polygons + point notes |
| ≥ 15 | building | building markers in viewport bbox |

District GeoJSON loads via `mapAbort`; environment load uses separate **`envLoadGen`** counter.

## Map rotation (leaflet-rotate)

- Enabled at `L.map()` creation: `rotate: true`, `touchRotate: true`, `shiftKeyRotate: true`  
- **Mobile:** two-finger twist  
- **Desktop:** Shift + drag  
- **Reset north:** `#reset-north-btn` in `#map-fabs` → `map.setBearing(0)`; disabled when bearing ≈ 0  
- Locate heading cone: `applyUserHeading()` subtracts `map.getBearing()` so direction stays correct when map is rotated  
- Do not enable built-in `rotateControl` — custom button matches locate/FAB stack  

## Otodom listings overlay (topbar → Offers)

- Topbar `#offers-toggle` opens `#offers-panel`  
- Decide / Anchors / Compare / Finalists / Weights UI removed (API left in place)  
- Filters: price max (default 650000), area min (50 m²), rooms 2–6+; city = locked map city (`localStorage` `cc_otodom_filters`)  
- Toggle `#otodom-show` → `POST /api/housing/otodom/pins` (read shared DB cache + bbox)  
- `#otodom-refresh` **Update offers** → `POST /api/housing/otodom/pins/refresh`  
- Orange `otodomLayer` markers; click → Open on Otodom / Save as offer  
- Debounced on `moveend` (read only); gen-counter ignores stale responses  

## Map modes (Comfort vs Environment)

- `localStorage` `cc_map_mode` = `comfort` \| `environment`  
- Toggle `#map-mode-toggle` in topbar  
- **Comfort:** polygon fill from note averages (`districtScores` → `feature.properties.score`)  
- **Environment:** fill from `environmentScores` via `riskColor(11 - risk)`; `#env-legend` + `riskSourceLayer`  
- District sheet meta: `formatEnvMeta()` shows risk, distances, downwind flag  

## Interaction

- Tap point center dot → select note (sheet; no auto-open form)  
- Tap district polygon → select district + housing slot  
- Tap building marker → select building  
- Tap empty map → city-level sheet, snap **peek**  
- Drag `#place-note-fab` onto map → new point note; drop via `map.mouseEventToLatLng`  
- Point influence circles (`L.circle`): `interactive: false`  
- Shift+click empty at building zoom → reverse-geocode building (desktop)  
- Selected district GeoJSON feature may call `layer.bringToFront()` — **only on vector layers**, not `L.LayerGroup`  

## Bottom sheet (≤899px)

- `.sheet-chrome` (handle, title, meta) always visible; `.sheet-body` scrolls  
- `setSheetSnap("peek" \| "half" \| "full")` — peek 5.5rem, half 45dvh, full 78dvh  
- Handle tap cycles half → full → peek; drag threshold 40px  
- Desktop ≥900px: `#sheet` side panel; snap classes ignored  

## FAB

- `#place-note-fab` sibling of `#sheet`, z-index 950  
- Mobile: `updateFabPosition()` from sheet height + safe-area (ResizeObserver)  
- Hidden while `#note-dialog` open  

## Voice input (notes)

- `voice-input.js`: `ru-RU` continuous recognition → appends to `#note-text`  
- Button `#note-voice-btn` shown only if `isVoiceInputSupported()`  
- Stop on dialog close  

## Layers / state

| Symbol | Purpose |
|--------|---------|
| `cityLayer` | City markers (unlocked) |
| `districtLayer` | GeoJSON polygons |
| `buildingLayer` | Building markers |
| `pointLayer` | Point notes (circles + center dots) |
| `riskSourceLayer` | Environment rings, wedges, markers |
| `context` | Current sheet target |
| `districtScores` / `buildingScores` | From `GET /api/cities/{id}/aggregates` |
| `environmentScores` / `environmentDetails` | From `GET /api/cities/{id}/environment` |
| `mapAbort` | Aborts district/building reload |
| `envLoadGen` | Ignores stale environment responses |

**Critical:** do not call `bringToFront()` on `L.LayerGroup` — throws and aborted env load.

## i18n

Always add EN + RU for new strings (`t(key)`, `data-i18n*`).
