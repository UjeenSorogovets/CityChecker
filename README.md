# Poland City Comfort Mapper

Personal map for notes and scores on Polish cities, neighborhoods, and buildings.

**Stack:** ASP.NET Core (.NET 10) + Leaflet SPA + PostgreSQL/PostGIS · Docker on port **8080** (local)

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

(`run.ps1` / `run.sh` create `.env` from `.env.example` if missing, then `docker compose up --build` in the foreground.)

## First run (Docker)

```bash
cp .env.example .env
# edit .env — set AUTH_JWT_SECRET (and Google vars if you use Google later)

docker compose down -v   # only when resetting DB / first PostGIS setup
docker compose up --build -d
curl -sI http://127.0.0.1:8080/
```

Local compose uses `ASPNETCORE_ENVIRONMENT=Development` and `AllowedHosts: *` so **localhost:8080** works. Production host restrictions apply only with the prod overlay (see below).

## Local dev (API on host)

```bash
docker compose up db -d
dotnet run --project src/CityChecker.Api
```

Open **http://localhost:5097**

## Deploy / update (VPS — https://ujeen.pl)

Connect to the server:

```bash
ssh root@ujeen.pl
# or: ssh root@YOUR_SERVER_IP
cd /opt/CityChecker
```

DNS: `A` / `AAAA` for **ujeen.pl** and **www.ujeen.pl** → your server IP.

TLS certs on the **host** (Certbot), mounted into Caddy. First time on a new server:

```bash
apt install certbot
certbot certonly --standalone -d ujeen.pl -d www.ujeen.pl
```

In `.env` on the server:

```env
DOMAIN=ujeen.pl
APP_PUBLIC_BASE_URL=https://ujeen.pl
AUTH_JWT_SECRET=…
CONTACT_EMAIL=you@example.com
```

Deploy (Caddy on **80/443**, API internal on **8080** only — not published publicly):

```bash
cd /opt/CityChecker
git pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
curl -sI https://ujeen.pl/
```

Or one command from the repo: `./run-prod.sh` (Linux) / `.\run-prod.ps1` (Windows).

**Firewall:** allow **80** and **443**; block public **8080** (prod compose does not expose it).

Google OAuth (optional): add `https://ujeen.pl` and `https://www.ujeen.pl` to Authorized JavaScript origins. Email/password works without Google.

After Certbot renew on the host:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml exec caddy caddy reload --config /etc/caddy/Caddyfile
```

Hard-refresh the browser after deploy. Migrations run on API startup; DB volume is kept.

## Useful commands

**Local** (default `docker-compose.yml`):

```bash
docker compose logs -f api
docker compose exec db psql -U citychecker -d citychecker
docker compose exec db psql -U citychecker -d citychecker -c 'SELECT COUNT(*) FROM "Districts";'
dotnet build src/CityChecker.Api/CityChecker.Api.csproj
```

**Production** (add `-f docker-compose.yml -f docker-compose.prod.yml` to every `docker compose` command):

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml logs -f caddy api
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
```

## Data files (before first Łódź import)

- `DataImports/Granice osiedli.csv`
- `DataImports/lodz-osiedla-polygons.json`
- `DataImports/wind-rose.json`
- `DataImports/lodz-pollution-sources.json`

Kraków/Warszawa polygon caches are imported automatically when those cities have no districts.

## Backup / restore (move to a new server)

Creates `backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz` with Postgres dump + `DataImports/` snapshot. Does **not** include `.env` (copy secrets separately). Env risk cache is omitted (recompute after restore).

**Windows:**

```powershell
.\scripts\backup.ps1
.\scripts\restore.ps1 backups\citychecker-backup-YYYYMMDD-HHMM.tar.gz
```

**Linux / VPS:**

```bash
./scripts/backup.sh
./scripts/restore.sh backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz
# production compose:
./scripts/backup.sh --prod
./scripts/restore.sh backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz --prod
```

After restore: ensure `.env` is present, then open Environment mode or `POST /api/admin/refresh-environment/{cityId}`.

Optional daily cron on the VPS: `0 3 * * * cd /opt/CityChecker && ./scripts/backup.sh --prod`

---

For architecture, API details, and AI-oriented context see **[AGENTS.md](AGENTS.md)**.
