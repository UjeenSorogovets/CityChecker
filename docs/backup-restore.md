# Backup and restore

Portable bundle for moving the app to a new server without losing districts, notes, housing data, or login.

## Why a DB dump (not files alone)

`DistrictId` values are generated at import time. Notes / picks / visits FK to those IDs. Re-importing GeoJSON on a new server **breaks** links unless `Districts` rows are restored from the dump.

| Data | In backup? |
|------|------------|
| Cities, Districts, Users, Notes, Buildings, housing | Yes (Postgres) |
| `DataImports/` snapshot | Yes (files) |
| Env risk cache (`DistrictEnvironments`, `CityEnvironmentSources`) | No — recompute |
| Otodom pin cache (`OtodomPinSets`, `OtodomPins`) | No — Refresh in Offers |
| OSM building footprints (`OsmBuildingFootprints`) | No — auto-seed on first Wołomin zoom or `POST /api/admin/refresh-building-footprints/{cityId}` |
| Import staging `districts_import_raw` | No |
| `.env` / JWT / Google / SSH secrets | **No** — copy separately |

## Bundle layout

`backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz`:

```
citychecker-backup-.../
  MANIFEST.json
  postgres.dump          # pg_dump -Fc
  DataImports/
```

## Backup

```bash
./scripts/backup.sh
./scripts/backup.sh --prod   # on the real server
```

On Windows use Git Bash / WSL (scripts are LF via `.gitattributes`).

Copy the archive off the server, and keep `.env` separately:

```bash
scp root@ujeen.pl:/opt/CityChecker/backups/citychecker-backup-*.tar.gz .
scp root@ujeen.pl:/opt/CityChecker/.env .env.prod.backup
```

### Daily cron (VPS)

```cron
0 3 * * * cd /opt/CityChecker && ./scripts/backup.sh --prod >> /var/log/citychecker-backup.log 2>&1
15 3 * * * find /opt/CityChecker/backups -name 'citychecker-backup-*.tar.gz' -mtime +14 -delete
```

## Restore

```bash
# ensure .env is in place first
./scripts/restore.sh backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz
./scripts/restore.sh backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz --prod
```

After restore:

1. Confirm `.env` (especially `AUTH_JWT_SECRET`)
2. Refresh environment cache: open **Environment** mode, or `POST /api/admin/refresh-environment/{cityId}`
