"""Smoke-test Otodom pins endpoint against local API."""
from __future__ import annotations

import json
import urllib.error
import urllib.request

BASE = "http://localhost:8080"
EMAIL = "otodom-test@citychecker.local"
PASSWORD = "otodom-test-password-9"


def req(method: str, path: str, body: dict | None = None, token: str | None = None, timeout: float = 120):
    data = None if body is None else json.dumps(body).encode()
    headers = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    r = urllib.request.Request(f"{BASE}{path}", data=data, headers=headers, method=method)
    with urllib.request.urlopen(r, timeout=timeout) as res:
        raw = res.read().decode()
        return res.status, json.loads(raw) if raw else None


def main() -> None:
    # homepage
    with urllib.request.urlopen(BASE + "/", timeout=15) as res:
        print("home", res.status)

    # auth
    try:
        _, login = req("POST", "/api/auth/login", {"email": EMAIL, "password": PASSWORD})
    except urllib.error.HTTPError:
        _, login = req("POST", "/api/auth/register", {"email": EMAIL, "password": PASSWORD})
    token = login["token"]
    print("auth ok")

    # Łódź filters + city-wide bbox (first load paginates + enriches — can take minutes)
    body = {
        "cityId": "11111111-1111-1111-1111-111111111111",
        "priceMax": 650000,
        "areaMin": 50,
        "rooms": ["TWO", "THREE", "FOUR", "FIVE", "SIX_OR_MORE"],
        "transaction": "SELL",
        "west": 19.2,
        "south": 51.6,
        "east": 19.7,
        "north": 51.9,
    }
    status, out = req("POST", "/api/housing/otodom/pins", body, token=token, timeout=300)
    print("status", status)
    print(
        "ok", out.get("ok"),
        "error", out.get("error"),
        "pins", len(out.get("pins") or []),
        "matched", out.get("totalMatched"),
        "listed", out.get("listed"),
    )
    pins = out.get("pins") or []
    for p in pins[:3]:
        print(
            "sample",
            p.get("lat"),
            p.get("lng") or p.get("lon"),
            p.get("price"),
            (p.get("title") or "")[:60],
            p.get("url"),
        )


if __name__ == "__main__":
    main()
