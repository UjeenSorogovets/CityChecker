# Local development

## One command

**Windows:** `.\run.ps1`  
**Linux / macOS:** `./run.sh`

Creates `.env` from `.env.example` if missing, then `docker compose up --build`.  
App: **http://localhost:8080** — sign up with email/password.

Local compose uses `ASPNETCORE_ENVIRONMENT=Development` and `AllowedHosts: *`.

## First run / reset DB

```bash
cp .env.example .env
# edit AUTH_JWT_SECRET (and Google vars if needed)

docker compose down -v   # wipes Postgres volume
docker compose up --build -d
curl -sI http://127.0.0.1:8080/
```

## API on host (DB in Docker)

```bash
docker compose up db -d
dotnet run --project src/CityChecker.Api
```

Open **http://localhost:5097**

## DataImports

Mounted read-only into the API container. Needed before first city import:

| File | Role |
|------|------|
| `Granice osiedli.csv` | Łódź district import |
| `lodz-osiedla-polygons.json` | Łódź polygons |
| `krakow-districts-polygons.json` | Kraków (auto if empty) |
| `warszawa-districts-polygons.json` | Warszawa (auto if empty) |
| `wroclaw-districts-polygons.json` | Wrocław 48 osiedla (auto if empty) |
| `gdansk-districts-polygons.json` | Gdańsk 35 dzielnice (auto if empty) |
| `wind-rose.json` | Environment wind frequencies |
| `lodz-pollution-sources.json` | Curated Łódź pollution points |

Regenerate polygon caches: `python DataImports/_fetch_osiedla_polygons.py`, `python DataImports/_fetch_krakow_warszawa.py`, `python DataImports/_fetch_wroclaw.py`, `python DataImports/_fetch_gdansk.py`.

## Useful commands

```bash
docker compose logs -f api
docker compose exec db psql -U citychecker -d citychecker
docker compose exec db psql -U citychecker -d citychecker -c 'SELECT COUNT(*) FROM "Districts";'
dotnet build src/CityChecker.Api/CityChecker.Api.csproj
```

## Gotchas

- Production `AllowedHosts` rejects `localhost` — use Development compose locally.
- Google Sign-In needs HTTPS + configured origins; email/password works on HTTP.
