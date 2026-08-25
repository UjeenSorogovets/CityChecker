"""Fetch OSM admin polygons for Gdańsk's 35 dzielnice into DataImports."""
import json
import time
import urllib.parse
import urllib.request
from pathlib import Path

OUT_DIR = Path(__file__).resolve().parent
UA = {"User-Agent": "CityChecker/1.0 (personal; gdansk-dzielnice)", "Accept-Language": "pl"}

# Official dzielnice (auxiliary units of Gmina Gdańsk)
DZIELNICE = [
    "Aniołki",
    "Brętowo",
    "Brzeźno",
    "Chełm",
    "Jasień",
    "Kokoszki",
    "Krakowiec-Górki Zachodnie",
    "Letnica",
    "Matarnia",
    "Młyniska",
    "Nowy Port",
    "Oliwa",
    "Olszynka",
    "Orunia Górna-Gdańsk Południe",
    "Orunia-Św. Wojciech-Lipce",
    "Osowa",
    "Piecki-Migowo",
    "Przeróbka",
    "Przymorze Małe",
    "Przymorze Wielkie",
    "Rudniki",
    "Siedlce",
    "Stogi",
    "Strzyża",
    "Suchanino",
    "Śródmieście",
    "Ujeścisko-Łostowice",
    "VII Dwór",
    "Wrzeszcz Dolny",
    "Wrzeszcz Górny",
    "Wyspa Sobieszewska",
    "Wzgórze Mickiewicza",
    "Zaspa-Młyniec",
    "Zaspa-Rozstaje",
    "Żabianka-Wejhera-Jelitkowo-Tysiąclecia",
]

CITY = "Gdańsk"
CITY_ASCII = "Gdansk"


def city_in_display(display: str) -> bool:
    return CITY in display or CITY_ASCII in display


def fetch_one(name: str):
    queries = [
        f"{name}, {CITY}, Poland",
        f"Dzielnica {name}, {CITY}, Poland",
        f"{name}, {CITY_ASCII}, Poland",
    ]
    for q in queries:
        url = (
            "https://nominatim.openstreetmap.org/search?q="
            + urllib.parse.quote(q)
            + "&format=json&polygon_geojson=1&limit=5"
        )
        req = urllib.request.Request(url, headers=UA)
        with urllib.request.urlopen(req, timeout=90) as resp:
            data = json.load(resp)
        for d in data:
            geo = d.get("geojson") or {}
            if d.get("class") == "boundary" and geo.get("type") in ("Polygon", "MultiPolygon"):
                display = d.get("display_name") or ""
                if city_in_display(display):
                    return d
        time.sleep(1.1)
    return None


def main():
    assert len(DZIELNICE) == 35, f"expected 35 dzielnice, got {len(DZIELNICE)}"
    features = []
    for name in DZIELNICE:
        pick = fetch_one(name)
        if not pick:
            print("MISS", name)
            continue
        features.append({
            "name": name,
            "osmType": pick.get("osm_type"),
            "osmId": pick.get("osm_id"),
            "displayName": pick.get("display_name"),
            "geometry": pick["geojson"],
        })
        print("OK", name, pick["geojson"]["type"])
        time.sleep(1.1)
    path = OUT_DIR / "gdansk-districts-polygons.json"
    path.write_text(
        json.dumps({"city": CITY, "districts": features}, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    print("wrote", path, "count", len(features), "/", len(DZIELNICE))


if __name__ == "__main__":
    main()
