#!/usr/bin/env python3
"""Anonymize cached OtodomPins: generic title/slug, no URL, numeric rooms.

Usage (from repo root):
  python scripts/anonymize_otodom_pins.py          # local docker db
  python scripts/anonymize_otodom_pins.py --prod   # via SSH on VPS
"""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

SQL = """
UPDATE "OtodomPins" SET
  "Title" = 'Offer',
  "Slug" = 'pin-' || "ExternalId"::text,
  "Url" = '',
  "Rooms" = CASE upper(trim(coalesce("Rooms", '')))
    WHEN 'ONE' THEN '1'
    WHEN 'TWO' THEN '2'
    WHEN 'THREE' THEN '3'
    WHEN 'FOUR' THEN '4'
    WHEN 'FIVE' THEN '5'
    WHEN 'SIX' THEN '6+'
    WHEN 'SIX_OR_MORE' THEN '6+'
    ELSE "Rooms"
  END;
SELECT COUNT(*) AS pins FROM "OtodomPins";
"""

def run_psql() -> int:
    cmd = [
        "docker", "compose", "exec", "-T", "db",
        "psql", "-U", "citychecker", "-d", "citychecker", "-v", "ON_ERROR_STOP=1",
    ]
    proc = subprocess.run(cmd, input=SQL, cwd=ROOT, text=True, capture_output=True)
    sys.stdout.write(proc.stdout)
    if proc.returncode != 0:
        sys.stderr.write(proc.stderr)
    return proc.returncode


def main() -> int:
    ap = argparse.ArgumentParser(description="Anonymize OtodomPins cache in Postgres")
    ap.add_argument("--prod", action="store_true", help="run on production VPS via scripts/remote.py")
    args = ap.parse_args()
    if args.prod:
        remote_cmd = r"""cd /opt/CityChecker && docker compose -f docker-compose.yml -f docker-compose.prod.yml exec -T db psql -U citychecker -d citychecker -v ON_ERROR_STOP=1 <<'EOSQL'
UPDATE "OtodomPins" SET
  "Title" = 'Offer',
  "Slug" = 'pin-' || "ExternalId"::text,
  "Url" = '',
  "Rooms" = CASE upper(trim(coalesce("Rooms", '')))
    WHEN 'ONE' THEN '1'
    WHEN 'TWO' THEN '2'
    WHEN 'THREE' THEN '3'
    WHEN 'FOUR' THEN '4'
    WHEN 'FIVE' THEN '5'
    WHEN 'SIX' THEN '6+'
    WHEN 'SIX_OR_MORE' THEN '6+'
    ELSE "Rooms"
  END;
SELECT COUNT(*) AS pins FROM "OtodomPins";
EOSQL"""
        proc = subprocess.run(
            [sys.executable, str(ROOT / "scripts" / "remote.py"), "--", remote_cmd],
            cwd=ROOT,
            text=True,
            capture_output=True,
        )
        sys.stdout.write(proc.stdout)
        if proc.returncode != 0:
            sys.stderr.write(proc.stderr)
            return proc.returncode
        print("anonymize_otodom_pins: done (prod)")
        return 0
    rc = run_psql()
    if rc == 0:
        print("anonymize_otodom_pins: done (local)")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
