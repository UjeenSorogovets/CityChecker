# Poland City Comfort Mapper

Personal map for notes and scores on Polish cities, neighborhoods, and buildings.

**Stack:** ASP.NET Core (.NET 10) + Leaflet + PostGIS · local **http://localhost:8080** · prod **https://ujeen.pl**

## Local

```powershell
.\run.ps1
```

```bash
./run.sh
```

First time: sign up with email/password. More: [docs/local-dev.md](docs/local-dev.md)

## Production update

```bash
ssh root@ujeen.pl
cd /opt/CityChecker
git pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
curl -sI https://ujeen.pl/
```

First-time server / TLS / nginx conflict: [docs/deploy.md](docs/deploy.md)

## Backup

```bash
./scripts/backup.sh --prod          # on VPS
./scripts/restore.sh backups/….tar.gz --prod
```

Details: [docs/backup-restore.md](docs/backup-restore.md)

## Docs

| | |
|--|--|
| [docs/](docs/README.md) | Human ops docs |
| [docs/ai/](docs/ai/README.md) | Detailed context for Cursor / AI agents |
| [AGENTS.md](AGENTS.md) | Short AI entry point |
