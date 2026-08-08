"""Emit scripts/_pg_dump_backup.sh with Unix newlines (run from repo root or scripts/)."""
from pathlib import Path

OUT = Path(__file__).resolve().parent / "_pg_dump_backup.sh"
OUT.write_text(
    """#!/bin/sh
set -e
pg_dump -U citychecker -d citychecker -Fc \\
  --exclude-table='"DistrictEnvironments"' \\
  --exclude-table='"CityEnvironmentSources"' \\
  --exclude-table=districts_import_raw \\
  -f /tmp/citychecker.dump
echo dump_ok
""",
    encoding="utf-8",
    newline="\n",
)
print("wrote", OUT)
