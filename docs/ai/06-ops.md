# Operations (for agents)

Human detail: [../local-dev.md](../local-dev.md), [../deploy.md](../deploy.md), [../backup-restore.md](../backup-restore.md), [../ssh-remote.md](../ssh-remote.md).

## Local run

```powershell
.\run.ps1
# or
docker compose up --build -d
curl -sI http://127.0.0.1:8080/
```

Host API without Docker: start `db` service only, then `dotnet run --project src/CityChecker.Api` (port from `launchSettings.json` / appsettings CORS).

## Prod deploy

From workstation (push to `main` first):

```bash
pip install -r scripts/requirements-remote.txt
python scripts/deploy.py
```

Or SSH then:

```bash
ssh root@ujeen.pl
cd /opt/CityChecker
git pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
curl -sI https://ujeen.pl/
```

- Caddy **80/443** → `api:8080` internal  
- TLS: host Certbot certs at `/etc/letsencrypt/live/ujeen.pl/`  
- After renew: `docker compose … exec caddy caddy reload --config /etc/caddy/Caddyfile`  

## Probe prod from workstation

Local `.env` (gitignored) with `SSH_HOST`, `SSH_USER`, `SSH_PASSWORD`, optional `SSH_PORT`:

```bash
pip install -r scripts/requirements-remote.txt
python scripts/remote.py --health
python scripts/remote.py -- 'cd /opt/CityChecker && docker compose -f docker-compose.yml -f docker-compose.prod.yml ps'
```

Never print SSH passwords. `--health` checks uptime, disk, compose ps, HTTPS headers.

## Backup / restore

```bash
./scripts/backup.sh --prod          # VPS
./scripts/backup.sh                 # local
./scripts/restore.sh backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz --prod
```

Windows: same `.sh` via Git Bash / WSL.

**Bundle:** `postgres.dump` (custom format) + `DataImports/` + `MANIFEST.json`  
**Excluded tables:** `DistrictEnvironments`, `CityEnvironmentSources`, `OtodomPinSets`, `OtodomPins`, `OsmBuildingFootprints`, `districts_import_raw`  
**Not included:** `.env` — copy separately using `.env.example` as template  

After restore: `POST /api/admin/refresh-environment/{cityId}` or open Environment mode.

## Secrets checklist

| Item | Where |
|------|--------|
| `.env` | gitignored — JWT, Google, DOMAIN, SSH_* |
| `.cursor/mcp.json` | gitignored — MCP local API password |
| `.env.example`, `.cursor/mcp.json.example` | Placeholders only |
| `backups/` | gitignored |
| Git history | Old commits may contain tracked `.env` — do not re-commit |

## Cursor MCP (local API)

```bash
cp .cursor/mcp.json.example .cursor/mcp.json
# venv + deps:
cd tools/citychecker_mcp && python -m venv .venv && pip install -r requirements.txt
```

Server: `tools/citychecker_mcp/server.py` (stdio). Env vars:

| Var | Default |
|-----|---------|
| `CITYCHECKER_BASE_URL` | `http://localhost:8080` |
| `CITYCHECKER_EMAIL` | `mcp@citychecker.local` |
| `CITYCHECKER_PASSWORD` | (set in mcp.json) |
| `CITYCHECKER_TOKEN` | optional skip login |

**Tools:** `health`, `list_cities`, `list_districts`, `get_environment`, `refresh_environment`  
Cursor server id often: `project-0-CityChecker-citychecker`

## Prod pitfall: host nginx vs Caddy

If `https://ujeen.pl` serves nginx default page or Caddy shows no published ports:

```bash
systemctl stop nginx && systemctl disable nginx
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --force-recreate caddy
```

Port **80/443** must bind to Caddy.
