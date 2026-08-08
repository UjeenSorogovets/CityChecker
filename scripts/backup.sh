#!/usr/bin/env sh
# Backup CityChecker → backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz
# Cron (VPS): 0 3 * * * cd /opt/CityChecker && ./scripts/backup.sh
set -e
ROOT="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

COMPOSE="docker compose"
if [ "${1:-}" = "--prod" ] || [ "${1:-}" = "-prod" ]; then
  COMPOSE="docker compose -f docker-compose.yml -f docker-compose.prod.yml"
fi

STAMP="$(date -u +%Y%m%d-%H%M)"
NAME="citychecker-backup-${STAMP}"
STAGE="${ROOT}/backups/${NAME}"
OUT="${ROOT}/backups/${NAME}.tar.gz"

mkdir -p "${STAGE}/DataImports"

echo "Waiting for db..."
$COMPOSE up -d db >/dev/null
$COMPOSE exec -T db pg_isready -U citychecker -d citychecker >/dev/null

echo "Dumping Postgres (custom format, env cache excluded)..."
$COMPOSE cp "${ROOT}/scripts/_pg_dump_backup.sh" db:/tmp/_pg_dump_backup.sh
$COMPOSE exec -T db sh /tmp/_pg_dump_backup.sh
$COMPOSE cp db:/tmp/citychecker.dump "${STAGE}/postgres.dump"
$COMPOSE exec -T db rm -f /tmp/citychecker.dump /tmp/_pg_dump_backup.sh

echo "Copying DataImports..."
if command -v rsync >/dev/null 2>&1; then
  rsync -a --exclude '__pycache__' --exclude '_inspect*.py' \
    "${ROOT}/DataImports/" "${STAGE}/DataImports/"
else
  cp -a "${ROOT}/DataImports/." "${STAGE}/DataImports/"
  rm -rf "${STAGE}/DataImports/__pycache__" 2>/dev/null || true
fi

PG_VER="$($COMPOSE exec -T db psql -U citychecker -d citychecker -tAc 'SHOW server_version;' | tr -d '[:space:]')"
GIT_SHA="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"

cat > "${STAGE}/MANIFEST.json" <<EOF
{
  "format": "citychecker-backup-v1",
  "createdAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "gitSha": "${GIT_SHA}",
  "postgresVersion": "${PG_VER}",
  "excludedTables": [
    "DistrictEnvironments",
    "CityEnvironmentSources",
    "districts_import_raw"
  ],
  "secretsChecklist": [
    "AUTH_JWT_SECRET",
    "GOOGLE_CLIENT_ID",
    "GOOGLE_ALLOWED_USER_ID",
    "CONTACT_EMAIL",
    "DOMAIN",
    "APP_PUBLIC_BASE_URL"
  ],
  "notes": "Copy .env separately. Env risk cache is omitted — refresh via POST /api/admin/refresh-environment/{cityId} or open Environment mode."
}
EOF

echo "Creating ${OUT}..."
tar -C "${ROOT}/backups" -czf "$OUT" "$NAME"
rm -rf "$STAGE"

echo "OK: $OUT"
ls -lh "$OUT"
