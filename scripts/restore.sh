#!/usr/bin/env sh
# Restore CityChecker from backups/citychecker-backup-....tar.gz
# Usage: ./scripts/restore.sh backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz [--prod]
set -e
ROOT="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

ARCHIVE="${1:-}"
if [ -z "$ARCHIVE" ] || [ "$ARCHIVE" = "--prod" ] || [ "$ARCHIVE" = "-prod" ]; then
  echo "Usage: $0 <backup.tar.gz> [--prod]" >&2
  exit 1
fi
shift

COMPOSE="docker compose"
if [ "${1:-}" = "--prod" ] || [ "${1:-}" = "-prod" ]; then
  COMPOSE="docker compose -f docker-compose.yml -f docker-compose.prod.yml"
fi

case "$ARCHIVE" in
  /*) ARCHIVE_ABS="$ARCHIVE" ;;
  *) ARCHIVE_ABS="${ROOT}/${ARCHIVE}" ;;
esac
if [ ! -f "$ARCHIVE_ABS" ]; then
  echo "Archive not found: $ARCHIVE_ABS" >&2
  exit 1
fi

EXTRACT="${ROOT}/backups/.restore-$$"
mkdir -p "$EXTRACT"
trap 'rm -rf "$EXTRACT"' EXIT

echo "Extracting $(basename "$ARCHIVE_ABS")..."
tar -xzf "$ARCHIVE_ABS" -C "$EXTRACT"
BUNDLE="$(find "$EXTRACT" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
if [ -z "$BUNDLE" ] || [ ! -f "${BUNDLE}/postgres.dump" ]; then
  echo "Invalid bundle: expected citychecker-backup-*/postgres.dump" >&2
  exit 1
fi

if [ -f "${BUNDLE}/MANIFEST.json" ]; then
  echo "MANIFEST:"
  cat "${BUNDLE}/MANIFEST.json"
  echo ""
fi

if [ ! -f "${ROOT}/.env" ]; then
  echo "WARNING: .env missing — copy secrets before relying on login/JWT (see MANIFEST secretsChecklist)."
fi

echo "Stopping api (db stays up)..."
$COMPOSE stop api 2>/dev/null || true
$COMPOSE up -d db >/dev/null
$COMPOSE exec -T db pg_isready -U citychecker -d citychecker >/dev/null

echo "Recreating database..."
$COMPOSE exec -T db psql -U citychecker -d postgres -v ON_ERROR_STOP=1 <<'SQL'
SELECT pg_terminate_backend(pid) FROM pg_stat_activity
  WHERE datname = 'citychecker' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS citychecker;
CREATE DATABASE citychecker OWNER citychecker;
SQL

echo "Restoring dump..."
$COMPOSE cp "${BUNDLE}/postgres.dump" db:/tmp/citychecker.dump
# --no-owner: dump may reference roles that don't exist in a fresh container
# pg_restore often exits 1 on non-fatal notices; verify core tables next
set +e
$COMPOSE exec -T db pg_restore -U citychecker -d citychecker --clean --if-exists --no-owner --no-acl \
  /tmp/citychecker.dump
set -e
$COMPOSE exec -T db psql -U citychecker -d citychecker -v ON_ERROR_STOP=1 -c \
  'SELECT COUNT(*) AS districts FROM "Districts"; SELECT COUNT(*) AS notes FROM "Notes";'
$COMPOSE exec -T db rm -f /tmp/citychecker.dump

if [ -d "${BUNDLE}/DataImports" ]; then
  echo "Syncing DataImports from bundle..."
  mkdir -p "${ROOT}/DataImports"
  if command -v rsync >/dev/null 2>&1; then
    rsync -a "${BUNDLE}/DataImports/" "${ROOT}/DataImports/"
  else
    cp -a "${BUNDLE}/DataImports/." "${ROOT}/DataImports/"
  fi
fi

echo "Starting stack..."
$COMPOSE up -d
echo ""
echo "Restore done."
echo "  - Ensure .env is present (AUTH_JWT_SECRET etc.)."
echo "  - Env layer cache was not in the dump — open Environment mode or:"
echo "      POST /api/admin/refresh-environment/{cityId}"
