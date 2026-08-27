#!/usr/bin/env python3
"""Self-check: Overpass geometry → closed GeoJSON ring (mirrors BuildingFootprintService.RingFromGeometry)."""
import json
import sys


def ring_from_geometry(el: dict) -> list[list[float]] | None:
    geom = el.get("geometry")
    if not isinstance(geom, list):
        return None
    ring = []
    for pt in geom:
        if "lat" not in pt or "lon" not in pt:
            continue
        ring.append([float(pt["lon"]), float(pt["lat"])])
    if len(ring) < 3:
        return None
    if ring[0] != ring[-1]:
        ring.append(ring[0][:])
    return ring if len(ring) >= 4 else None


def main() -> int:
    open_ring = {"geometry": [{"lat": 1.0, "lon": 2.0}, {"lat": 1.0, "lon": 3.0}, {"lat": 2.0, "lon": 3.0}]}
    r = ring_from_geometry(open_ring)
    assert r is not None and len(r) == 4 and r[0] == r[-1] == [2.0, 1.0], r
    assert ring_from_geometry({"geometry": [{"lat": 1, "lon": 2}]}) is None
    print("ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
