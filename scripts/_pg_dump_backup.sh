#!/bin/sh
set -e
pg_dump -U citychecker -d citychecker -Fc \
  --exclude-table='"DistrictEnvironments"' \
  --exclude-table='"CityEnvironmentSources"' \
  --exclude-table='"OtodomPinSets"' \
  --exclude-table='"OtodomPins"' \
  --exclude-table='"OsmBuildingFootprints"' \
  --exclude-table=districts_import_raw \
  -f /tmp/citychecker.dump
echo dump_ok
