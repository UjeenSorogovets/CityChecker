# Poland City Comfort Mapper

Personal map for notes and scores on Polish cities, neighborhoods, and buildings.

**Stack:** ASP.NET Core (.NET 10) + Leaflet SPA + PostgreSQL/PostGIS · Docker on port **8080**

## Run locally (one command)

**Windows (PowerShell):**

```powershell
.\run.ps1
```

**Linux / macOS:**

```bash
./run.sh
```

Opens **http://localhost:8080** when the API is up. First time: sign up with email/password.

(`run.ps1` / `run.sh` create `.env` from `.env.example` if missing, then `docker compose up --build`.)

## First run (Docker)

```bash
cp .env.example .env
# edit .env — set AUTH_JWT_SECRET (and Google vars if you use Google later)

docker compose down -v   # only when resetting DB / first PostGIS setup
docker compose up --build -d
```

Open **http://localhost:8080** — sign up with email/password.

## Local dev (API on host)

```bash
docker compose up db -d
dotnet run --project src/CityChecker.Api
```

Open **http://localhost:5097**

## Deploy / update (VPS — https://ujeen.pl)

DNS: `A` / `AAAA` for **ujeen.pl** and **www.ujeen.pl** → your server IP.

In `.env` on the server:

```env
DOMAIN=ujeen.pl
ACME_EMAIL=you@example.com
APP_PUBLIC_BASE_URL=https://ujeen.pl
AUTH_JWT_SECRET=…
```

Deploy (Caddy on **80/443**, API internal on **8080** only):

```bash
cd /opt/CityChecker
git pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
curl -sI https://ujeen.pl/
```

Or one command: `./run-prod.sh` (Linux) / `.\run-prod.ps1` (Windows).

**Firewall:** allow **80** and **443**; block public **8080** (API is not published in prod compose).

Google OAuth (optional): add `https://ujeen.pl` and `https://www.ujeen.pl` to Authorized JavaScript origins.

Hard-refresh the browser after deploy. Migrations run on API startup; DB volume is kept.

## Local Docker (no TLS)

```bash
docker compose up --build -d
curl -sI http://127.0.0.1:8080/
```

## Useful commands

```bash
# logs
docker compose logs -f api

# DB shell
docker compose exec db psql -U citychecker -d citychecker

# district count
docker compose exec db psql -U citychecker -d citychecker -c 'SELECT COUNT(*) FROM "Districts";'

# rebuild after code change (local)
dotnet build src/CityChecker.Api/CityChecker.Api.csproj
```

## Data files (before first Łódź import)

- `DataImports/Granice osiedli.csv`
- `DataImports/lodz-osiedla-polygons.json`

Kraków/Warszawa polygon caches are imported automatically when those cities have no districts.

---

For architecture, API details, and AI-oriented context see **[AGENTS.md](AGENTS.md)**.
