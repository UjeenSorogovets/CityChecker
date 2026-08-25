"""Fetch OSM admin polygons for Wrocław's 48 osiedla into DataImports."""
import json
import time
import urllib.parse
import urllib.request
from pathlib import Path

OUT_DIR = Path(__file__).resolve().parent
UA = {"User-Agent": "CityChecker/1.0 (personal; wroclaw-osiedla)", "Accept-Language": "pl"}

# Official auxiliary units (osiedla) — RM XX/419/16; not the obsolete 5 dzielnice
OSIEDLA = [
    "Gajowice",
    "Gądów-Popowice Południowe",
    "Grabiszyn-Grabiszynek",  # OSM/official spelling (not Grabieszyn)
    "Jerzmanowo-Jarnołtów-Strachowice-Osiniec",
    "Kuźniki",
    "Leśnica",
    "Maślice",
    "Muchobór Mały",
    "Muchobór Wielki",
    "Nowy Dwór",
    "Oporów",
    "Pilczyce-Kozanów-Popowice Północne",
    "Pracze Odrzańskie",
    "Żerniki",
    "Bieńkowice",
    "Borek",
    "Brochów",
    "Gaj",
    "Huby",
    "Jagodno",
    "Klecina",
    "Krzyki-Partynice",
    "Księże",
    "Ołtaszyn",
    "Powstańców Śląskich",
    "Przedmieście Oławskie",
    "Tarnogaj",
    "Wojszyce",
    "Karłowice-Różanka",
    "Kleczków",
    "Kowale",
    "Lipa Piotrowska",
    "Osobowice-Rędzin",
    "Pawłowice",
    "Polanowice-Poświętne-Ligota",
    "Psie Pole-Zawidawie",
    "Sołtysowice",
    "Swojczyce-Strachocin-Wojnów",
    "Świniary",
    "Widawa",
    "Przedmieście Świdnickie",
    "Stare Miasto",
    "Szczepin",
    "Biskupin-Sępolno-Dąbie-Bartoszowice",
    "Nadodrze",
    "Ołbin",
    "Plac Grunwaldzki",
    "Zacisze-Zalesie-Szczytniki",
]

CITY = "Wrocław"
CITY_ASCII = "Wroclaw"


def city_in_display(display: str) -> bool:
    return CITY in display or CITY_ASCII in display or "Wrocław" in display


def fetch_one(name: str):
    queries = [
        f"{name}, {CITY}, Poland",
        f"Osiedle {name}, {CITY}, Poland",
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
    assert len(OSIEDLA) == 48, f"expected 48 osiedla, got {len(OSIEDLA)}"
    features = []
    for name in OSIEDLA:
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
    path = OUT_DIR / "wroclaw-districts-polygons.json"
    path.write_text(
        json.dumps({"city": CITY, "districts": features}, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    print("wrote", path, "count", len(features), "/", len(OSIEDLA))


if __name__ == "__main__":
    main()
