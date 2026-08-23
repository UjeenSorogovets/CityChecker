# Conventions for AI agents

## Default stance

- **Lazy senior / minimal diff** — reuse existing patterns; no new dependencies unless necessary  
- **English only** in code, comments, commits, and UI source strings (RU lives in `i18n.js`)  
- **No commits/push** unless the user explicitly asks  
- Prefer deletion over abstraction; fewest files possible  

## Backend endpoints

- Static classes: `Map*Endpoints(this WebApplication app)`  
- Route groups: `.RequireAuthorization()`  
- Handlers start with `user.EnsureOwner(config)` (or `GetUserId()` in housing handlers)  
- DTOs in `Dtos/Dtos.cs`; enums serialized as strings (`JsonStringEnumConverter`)  
- Register in `Program.cs`: `app.Map*Endpoints()`  

## Frontend

- Edit `wwwroot` directly — **no bundler**  
- After point note save/delete: `reloadDistrictColors()` + `loadPointNotes()`  
- New UI strings: EN + RU in `i18n.js` + `data-i18n` / `data-i18n-aria` / `data-i18n-title` in HTML  
- CSS: mobile-first; bottom sheet ≤899px; side panel ≥900px  
- After SPA JS changes: bump `app.js?v=…` in `index.html` (currently `env5`)  

## Auth UI

- Wire `#password-form` **before** Google GIS loads (`initAuth`) — prevents GET submit leaking credentials in URL  
- `scrubLeakedAuthQuery()` strips `?email=` / `?password=` from history  
- Google button hidden until valid `googleClientId` from `/api/config`  
- Token in `sessionStorage` (`cc_id_token`); `api()` clears expired JWT client-side  

## Secrets

- Never commit `.env` or `.cursor/mcp.json`  
- Never print `SSH_PASSWORD`, JWT secrets, or passwords in output  
- Prod SSH: `python scripts/remote.py` reads `SSH_*` from local `.env` only  

## Migrations

```bash
dotnet ef migrations add Name --project src/CityChecker.Api
```

Applied automatically on API startup (`MigrateAsync()`).

## Common change recipes

### Add API endpoint

1. DTO in `Dtos.cs` if needed  
2. Handler in `Endpoints/*.cs`  
3. `app.Map*Endpoints()` in `Program.cs`  
4. Wire `api.js` / feature JS  

### Add DB column

1. Entity under `Data/Entities/`  
2. `dotnet ef migrations add …`  
3. DTOs + endpoints  
4. Restart API (migration runs on startup)  

### Change map behavior

Primary: `wwwroot/js/app.js`. Housing: `housing.js`. Styles: `app.css`. Environment: [04-environment.md](04-environment.md).

### Change environment scoring / sources

`Services/EnvironmentService.cs` + optional `DataImports/lodz-pollution-sources.json` / `wind-rose.json`. Force refresh: admin endpoint or Environment toggle after cache expiry.
