# Known gotchas

## Host / Docker

- **Invalid Hostname on localhost** — local compose must use `Development`; prod `AllowedHosts` rejects `localhost` → HTTP 400  
- **Host nginx vs Caddy** — nginx on 80 blocks Caddy; stop/disable host nginx, recreate Caddy container  
- **Google on IP/HTTP** — GIS fails; use email/password or HTTPS domain  
- **Prod IP access** — bare IP redirects to `App:PublicBaseUrl`  

## Auth

- **Password form GET leak** — if JS not ready, form could GET-submit credentials into URL; fixed by early `onsubmit` + `scrubLeakedAuthQuery()`  
- **Google token** — frontend stores Google **ID token** directly as Bearer (not exchanged server-side)  
- **`MapInboundClaims = false`** — use `GetUserId()`, not `ClaimTypes`  
- **Note edits** — `AuthorGoogleId` must match; password user `sub` = `UserId` GUID string  
- **`NoteLevel` value 1 = Point** — old district notes deleted in migration  

## Map / Leaflet

- **No `fitBounds` on district load** — caused mobile zoom loop; use city `setView` first  
- **`setMinZoom(11)` after centering** on locked city  
- **FAB drop** — `map.mouseEventToLatLng`, not manual rect math  
- **Locked city** — map click does not create notes; FAB only for new points  
- **`L.LayerGroup.bringToFront`** — **not a function**; threw and aborted environment load. OK on individual GeoJSON layers only  
- **District reload vs env** — `mapAbort` must not cancel env fetch; use `envLoadGen`  

## Environment layer

- Wedges use **downwind** bearing (`windBearing` = plume), not `windFrom` alone  
- First load can take **10–60s** (Overpass); MCP/API timeout should be ≥120s  
- Orange triangles on OSM basemap ≠ our markers (ours: circleMarkers + dashed rings in `risk` pane)  
- After JS fixes: hard-refresh or bump `app.js?v=` in `index.html`  
- Empty env UI often means JS error mid-load (check console for `bringToFront` or aborted fetch)  

## Otodom overlay

- **No official API** — personal-use proxy only; may break if Otodom changes Next.js paths / blocks bots  
- **SearchMapPins** GraphQL uses Apollo APQ with **client-computed** sha256 hashes — we use `_next/data/{buildId}/…` instead  
- Request: `cityId` + filters (or optional `searchUrl`) + bbox; seeded city paths hardcoded  
- **Pagination** up to ~720 ads; cold load can take 1–3 min (detail coords per listing); then cache ~5 min and pan only refilters  
- **buildId** scraped from Otodom HTML and cached ~6h; if pins fail, wait or clear API memory cache  
- Pin locations are often **approximate** (Otodom map radius)  
- Do not republish Otodom data; open official listing URLs in a new tab  

## Data / imports

- **District re-import orphans FKs** — restore DB dump, not GeoJSON alone  
- **Missing polygon JSON** on fresh clone — run `DataImports/_fetch_*.py` before import  
- **Backup without env cache** — refresh environment after restore  

## Manual verify checklist

- [ ] Sign up / sign in on http://localhost:8080  
- [ ] Pick city → districts load, FAB above sheet  
- [ ] Drag FAB → point note; district comfort fill updates  
- [ ] Environment toggle → rings/wedges + sheet risk meta  
- [ ] Voice button (Chrome) appends Russian text to note  
- [ ] Decide panel: anchor, shortlist, compare  
- [ ] Desktop ≥900px: side panel, FAB bottom-right  
