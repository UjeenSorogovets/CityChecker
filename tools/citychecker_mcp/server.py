"""CityChecker MCP — tools against local/prod API (stdio)."""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from typing import Any

from mcp.server.fastmcp import FastMCP

mcp = FastMCP("citychecker")

BASE = os.environ.get("CITYCHECKER_BASE_URL", "http://localhost:8080").rstrip("/")
EMAIL = os.environ.get("CITYCHECKER_EMAIL", "mcp@citychecker.local")
PASSWORD = os.environ.get("CITYCHECKER_PASSWORD", "mcp-local-dev-password")
_token: str | None = os.environ.get("CITYCHECKER_TOKEN") or None


def _request(
    method: str,
    path: str,
    *,
    body: dict | None = None,
    auth: bool = True,
    timeout: float = 120,
) -> Any:
    global _token
    url = f"{BASE}{path}"
    data = None if body is None else json.dumps(body).encode("utf-8")
    headers: dict[str, str] = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if auth:
        if not _token:
            _ensure_token()
        headers["Authorization"] = f"Bearer {_token}"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            raw = res.read().decode("utf-8")
            if not raw:
                return None
            return json.loads(raw)
    except urllib.error.HTTPError as e:
        err_body = e.read().decode("utf-8", errors="replace")
        if e.code == 401 and auth:
            _token = None
            _ensure_token()
            headers["Authorization"] = f"Bearer {_token}"
            req = urllib.request.Request(url, data=data, headers=headers, method=method)
            with urllib.request.urlopen(req, timeout=timeout) as res:
                raw = res.read().decode("utf-8")
                return json.loads(raw) if raw else None
        raise RuntimeError(f"{method} {path} -> {e.code}: {err_body[:500]}") from e


def _ensure_token() -> None:
    global _token
    if _token:
        return
    try:
        out = _request(
            "POST",
            "/api/auth/login",
            body={"email": EMAIL, "password": PASSWORD},
            auth=False,
            timeout=30,
        )
        _token = out["token"]
        return
    except Exception:
        pass
    out = _request(
        "POST",
        "/api/auth/register",
        body={"email": EMAIL, "password": PASSWORD},
        auth=False,
        timeout=30,
    )
    _token = out["token"]


def _ok(data: Any) -> str:
    return json.dumps(data, indent=2, default=str)


@mcp.tool()
def health() -> str:
    """Check that CityChecker is reachable (public homepage)."""
    req = urllib.request.Request(f"{BASE}/", method="GET")
    with urllib.request.urlopen(req, timeout=15) as res:
        return _ok({"baseUrl": BASE, "status": res.status, "contentType": res.headers.get("Content-Type")})


@mcp.tool()
def list_cities() -> str:
    """List seeded cities with district counts."""
    return _ok(_request("GET", "/api/cities"))


@mcp.tool()
def get_environment(city_id: str) -> str:
    """Get cached environmental risk for a city (district scores + source feature count).

    city_id: GUID, e.g. Łódź 11111111-1111-1111-1111-111111111111,
    Kraków 22222222-2222-2222-2222-222222222222,
    Warszawa 33333333-3333-3333-3333-333333333333.
    First call may take ~10–60s if Overpass must run.
    """
    data = _request("GET", f"/api/cities/{city_id}/environment", timeout=120)
    sources = data.get("sources") or {}
    features = sources.get("features") or []
    districts = data.get("districts") or []
    top = sorted(districts, key=lambda d: d.get("envRiskOverall") or 0, reverse=True)[:10]
    return _ok(
        {
            "computedAt": data.get("computedAt"),
            "districtCount": len(districts),
            "sourceFeatureCount": len(features),
            "topRiskDistricts": top,
            "districts": districts,
        }
    )


@mcp.tool()
def refresh_environment(city_id: str) -> str:
    """Force recompute of environmental risk for a city (Overpass + wind rose). May take ~10–60s."""
    return _ok(_request("POST", f"/api/admin/refresh-environment/{city_id}", timeout=120))


@mcp.tool()
def list_districts(city_id: str) -> str:
    """List districts for a city (id, name, area)."""
    return _ok(_request("GET", f"/api/cities/{city_id}/districts"))


if __name__ == "__main__":
    mcp.run()
