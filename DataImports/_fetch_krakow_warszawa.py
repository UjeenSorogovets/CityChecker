"""Fetch OSM admin polygons for Kraków and Warszawa dzielnice into DataImports."""
import json
import time
import urllib.parse
import urllib.request
from pathlib import Path

OUT_DIR = Path(__file__).resolve().parent
UA = {"User-Agent": "CityChecker/1.0 (personal; multi-city-districts)", "Accept-Language": "pl"}

# Official city dzielnice (boroughs) — not micro-osiedla
CITIES = {
    "Kraków": [
        "Stare Miasto", "Grzegórzki", "Prądnik Czerwony", "Prądnik Biały", "Krowodrza",
        "Bronowice", "Zwierzyniec", "Dębniki", "Łagiewniki-Borek Fałęcki", "Swoszowice",
        "Podgórze Duchackie", "Bieżanów-Prokocim", "Podgórze", "Czyżyny", "Mistrzejowice",
        "Bieńczyce", "Wzgórza Krzesławickie", "Nowa Huta",
    ],
    "Warszawa": [
        "Bemowo", "Białołęka", "Bielany", "Mokotów", "Ochota", "Praga-Południe", "Praga-Północ",
        "Rembertów", "Śródmieście", "Targówek", "Ursus", "Ursynów", "Wawer", "Wesoła",
        "Wilanów", "Włochy", "Wola", "Żoliborz",
    ],
}


def fetch_one(city: str, name: str):
    queries = [
        f"{name}, {city}, Poland",
        f"Dzielnica {name}, {city}, Poland",
    ]
    for q in queries:
        url = "https://nominatim.openstreetmap.org/search?q=" + urllib.parse.quote(q) + "&format=json&polygon_geojson=1&limit=5"
        req = urllib.request.Request(url, headers=UA)
        with urllib.request.urlopen(req, timeout=90) as resp:
            data = json.load(resp)
        for d in data:
            geo = d.get("geojson") or {}
            if d.get("class") == "boundary" and geo.get("type") in ("Polygon", "MultiPolygon"):
                display = d.get("display_name") or ""
                if city in display or city.replace("ów", "ow") in display:
                    return d
        time.sleep(1.1)
    return None


def main():
    for city, names in CITIES.items():
        features = []
        for name in names:
            pick = fetch_one(city, name)
            if not pick:
                print("MISS", city, name)
                continue
            features.append({
                "name": name,
                "osmType": pick.get("osm_type"),
                "osmId": pick.get("osm_id"),
                "displayName": pick.get("display_name"),
                "geometry": pick["geojson"],
            })
            print("OK", city, name, pick["geojson"]["type"])
            time.sleep(1.1)
        slug = "krakow" if city == "Kraków" else "warszawa"
        path = OUT_DIR / f"{slug}-districts-polygons.json"
        path.write_text(json.dumps({"city": city, "districts": features}, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        print("wrote", path, "count", len(features))


if __name__ == "__main__":
    main()
