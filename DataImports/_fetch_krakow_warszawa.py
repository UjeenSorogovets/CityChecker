"""Fetch OSM admin polygons for Kraków and Warszawa dzielnice (+ WA suburbs) into DataImports."""
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

# Nearby towns treated as Warszawa map districts (separate gminas)
WARSAW_SUBURBS = [
    "Ząbki",
    "Marki",
    "Zielonka",
    "Kobyłka",
    "Wołomin",
    "Łomianki",
    "Piaseczno",
    "Legionowo",
    "Konstancin-Jeziorna",
]

# ponytail: Zielonka admin includes huge eastern forest; keep built-up west only
ZIELONKA_MAX_LON = 21.22


def fetch_boundary(queries: list[str], name_ok):
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
                if name_ok(display):
                    return d
        time.sleep(1.1)
    return None


def fetch_dzielnica(city: str, name: str):
    return fetch_boundary(
        [f"{name}, {city}, Poland", f"Dzielnica {name}, {city}, Poland"],
        lambda display: city in display or city.replace("ów", "ow") in display,
    )


def fetch_suburb(name: str):
    return fetch_boundary(
        [f"{name}, mazowieckie, Poland", f"{name}, Poland"],
        lambda display: name in display,
    )


def geom_bbox(geom: dict):
    pts = list(_iter_coords(geom))
    if not pts:
        return None
    lons = [p[0] for p in pts]
    lats = [p[1] for p in pts]
    return min(lons), min(lats), max(lons), max(lats)


def _iter_coords(geom: dict):
    t = geom["type"]
    c = geom["coordinates"]
    if t == "Polygon":
        for ring in c:
            yield from ring
    elif t == "MultiPolygon":
        for poly in c:
            for ring in poly:
                yield from ring


def _clip_ring_max_lon(ring: list, max_lon: float) -> list:
    """Clip ring to lon <= max_lon (half-plane)."""
    if len(ring) < 2:
        return []
    out = []
    for i in range(len(ring) - 1):
        x1, y1 = ring[i][0], ring[i][1]
        x2, y2 = ring[i + 1][0], ring[i + 1][1]
        in1, in2 = x1 <= max_lon, x2 <= max_lon
        if in1:
            out.append([x1, y1])
        if in1 != in2:
            # edge crosses max_lon
            t = (max_lon - x1) / (x2 - x1) if x2 != x1 else 0.0
            out.append([max_lon, y1 + t * (y2 - y1)])
    if not out:
        return []
    if out[0] != out[-1]:
        out.append(out[0][:])
    return out if len(out) >= 4 else []


def clip_geom_max_lon(geom: dict, max_lon: float) -> dict | None:
    t = geom["type"]
    if t == "Polygon":
        rings = []
        for ring in geom["coordinates"]:
            clipped = _clip_ring_max_lon(ring, max_lon)
            if clipped:
                rings.append(clipped)
        if not rings:
            return None
        return {"type": "Polygon", "coordinates": rings}
    if t == "MultiPolygon":
        polys = []
        for poly in geom["coordinates"]:
            rings = []
            for ring in poly:
                clipped = _clip_ring_max_lon(ring, max_lon)
                if clipped:
                    rings.append(clipped)
            if rings:
                polys.append(rings)
        if not polys:
            return None
        if len(polys) == 1:
            return {"type": "Polygon", "coordinates": polys[0]}
        return {"type": "MultiPolygon", "coordinates": polys}
    return None


def feature_from(name: str, pick: dict) -> dict:
    geom = pick["geojson"]
    if name == "Zielonka":
        before = geom_bbox(geom)
        clipped = clip_geom_max_lon(geom, ZIELONKA_MAX_LON)
        if clipped is None:
            print("WARN Zielonka clip emptied geometry — keeping original")
        else:
            after = geom_bbox(clipped)
            print(
                "CLIP Zielonka lon<=",
                ZIELONKA_MAX_LON,
                "bbox",
                tuple(round(x, 4) for x in before) if before else None,
                "->",
                tuple(round(x, 4) for x in after) if after else None,
            )
            geom = clipped
    return {
        "name": name,
        "osmType": pick.get("osm_type"),
        "osmId": pick.get("osm_id"),
        "displayName": pick.get("display_name"),
        "geometry": geom,
    }


def fetch_list(names: list[str], fetcher, label: str) -> list[dict]:
    features = []
    for name in names:
        pick = fetcher(name)
        if not pick:
            print("MISS", label, name)
            continue
        features.append(feature_from(name, pick))
        print("OK", label, name, features[-1]["geometry"]["type"])
        time.sleep(1.1)
    return features


def main():
    for city, names in CITIES.items():
        features = fetch_list(names, lambda n, c=city: fetch_dzielnica(c, n), city)
        if city == "Warszawa":
            features.extend(fetch_list(WARSAW_SUBURBS, fetch_suburb, "suburb"))
        slug = "krakow" if city == "Kraków" else "warszawa"
        path = OUT_DIR / f"{slug}-districts-polygons.json"
        path.write_text(
            json.dumps({"city": city, "districts": features}, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
        print("wrote", path, "count", len(features))


if __name__ == "__main__":
    main()
