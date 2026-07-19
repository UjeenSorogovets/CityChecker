import csv
import json
import time
import urllib.parse
import urllib.request
from pathlib import Path

CSV = Path(r"E:\Work\CityChecker\DataImports\Granice osiedli.csv")
OUT = Path(r"E:\Work\CityChecker\DataImports\lodz-osiedla-polygons.json")
UA = {"User-Agent": "CityChecker/1.0 (personal; lodz-osiedla-import)", "Accept-Language": "pl"}

text = CSV.read_text(encoding="utf-8-sig")
names = sorted({r["Osiedla"].strip() for r in csv.DictReader(text.splitlines(), delimiter=";") if r.get("Osiedla")})
print("osiedla", len(names))

features = []
for name in names:
    q = urllib.parse.quote(f"{name}, Łódź, Poland")
    url = f"https://nominatim.openstreetmap.org/search?q={q}&format=json&polygon_geojson=1&limit=5"
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=90) as resp:
        data = json.load(resp)

    # Prefer administrative boundary relation with Polygon/MultiPolygon
    pick = None
    for d in data:
        geo = d.get("geojson") or {}
        if d.get("class") == "boundary" and geo.get("type") in ("Polygon", "MultiPolygon"):
            # Prefer display_name containing Łódź
            if "Łódź" in (d.get("display_name") or "") or "Lodz" in (d.get("display_name") or ""):
                pick = d
                break
    if pick is None:
        for d in data:
            geo = d.get("geojson") or {}
            if geo.get("type") in ("Polygon", "MultiPolygon"):
                pick = d
                break

    if pick is None:
        print("MISS", name)
        continue

    features.append({
        "name": name,
        "osmType": pick.get("osm_type"),
        "osmId": pick.get("osm_id"),
        "displayName": pick.get("display_name"),
        "geometry": pick["geojson"],
    })
    print("OK", name, pick["geojson"]["type"], pick.get("osm_type"), pick.get("osm_id"))
    time.sleep(1.1)

OUT.write_text(json.dumps({"city": "Łódź", "districts": features}, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print("wrote", OUT, "bytes", OUT.stat().st_size, "count", len(features))
