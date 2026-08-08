"""Headless screenshot of Łódź Environment mode for debugging."""
from __future__ import annotations

import json
import os
import re

from playwright.sync_api import sync_playwright

OUT = r"E:\Work\CityChecker\screenshots"
os.makedirs(OUT, exist_ok=True)
SHOT = os.path.join(OUT, "lodz-environment.png")
DEBUG = os.path.join(OUT, "lodz-environment-debug.json")

EMAIL = os.environ.get("CITYCHECKER_EMAIL", "mcp@citychecker.local")
PASSWORD = os.environ.get("CITYCHECKER_PASSWORD", "mcp-local-dev-password")
BASE = os.environ.get("CITYCHECKER_BASE_URL", "http://localhost:8080")


def main() -> None:
    console_logs: list[dict] = []
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        context = browser.new_context(viewport={"width": 1400, "height": 900})
        page = context.new_page()
        page.on("console", lambda msg: console_logs.append({"type": msg.type, "text": msg.text}))
        page.on("pageerror", lambda err: console_logs.append({"type": "pageerror", "text": str(err)}))

        page.goto(BASE, wait_until="domcontentloaded")
        page.wait_for_selector("#login-email", timeout=20000)

        page.fill("#login-email", EMAIL)
        page.fill("#login-password", PASSWORD)
        page.click("#auth-submit")
        page.wait_for_timeout(2000)

        # If login failed (user missing), switch to signup
        gate = page.locator("#auth-gate")
        if gate.count() and "hidden" not in (gate.get_attribute("class") or ""):
            if page.locator("#tab-signup").count():
                page.locator("#tab-signup").click()
            page.fill("#login-email", EMAIL)
            page.fill("#login-password", PASSWORD)
            page.click("#auth-submit")
            page.wait_for_timeout(2000)

        page.wait_for_selector("#map", timeout=20000)
        page.wait_for_timeout(1000)

        picker = page.locator("#city-picker")
        if picker.count() and "hidden" not in (picker.get_attribute("class") or ""):
            # Match Łódź / Lodz
            clicked = False
            for btn in page.locator("#city-picker-list button").all():
                text = btn.inner_text()
                if re.search(r"odz|Łódź|Lodz", text, re.I):
                    btn.click()
                    clicked = True
                    break
            if not clicked and page.locator("#city-picker-list button").count():
                page.locator("#city-picker-list button").first.click()
            page.wait_for_timeout(3000)
        else:
            page.wait_for_timeout(1500)

        page.evaluate("() => localStorage.setItem('cc_map_mode', 'environment')")
        btn = page.locator("#map-mode-toggle")
        for _ in range(4):
            txt = (btn.inner_text() or "").strip().lower()
            if "environment" in txt:
                break
            btn.click()
            page.wait_for_timeout(700)

        try:
            with page.expect_response(
                lambda r: "/environment" in r.url and r.status == 200,
                timeout=60000,
            ):
                # trigger already happened; also poke toggle to force reload
                btn.click()
                page.wait_for_timeout(400)
                btn.click()
        except Exception as e:
            console_logs.append({"type": "wait", "text": f"env response wait: {e}"})

        page.wait_for_timeout(3500)

        # Click a central district if possible
        try:
            box = page.locator("#map").bounding_box()
            if box:
                page.mouse.click(box["x"] + box["width"] * 0.45, box["y"] + box["height"] * 0.4)
                page.wait_for_timeout(1500)
        except Exception as e:
            console_logs.append({"type": "click", "text": str(e)})

        debug = page.evaluate(
            """() => {
          const riskPane = document.querySelector('.leaflet-risk-pane');
          const legend = document.getElementById('env-legend');
          const svgs = riskPane ? riskPane.querySelectorAll('svg').length : 0;
          const paths = riskPane ? riskPane.querySelectorAll('path').length : 0;
          const circles = riskPane ? riskPane.querySelectorAll('circle').length : 0;
          return {
            mapModeBtn: document.getElementById('map-mode-toggle')?.textContent,
            legendHidden: legend?.classList.contains('hidden'),
            legendText: legend?.innerText?.slice(0, 240),
            sheetTitle: document.getElementById('sheet-title')?.textContent,
            sheetMeta: document.getElementById('sheet-meta')?.textContent,
            riskPaneExists: !!riskPane,
            riskPaneDisplay: riskPane ? getComputedStyle(riskPane).display : null,
            riskPaneVisibility: riskPane ? getComputedStyle(riskPane).visibility : null,
            riskPaneZ: riskPane ? getComputedStyle(riskPane).zIndex : null,
            riskSvgCount: svgs,
            riskPathCount: paths,
            riskCircleCount: circles,
            riskPaneHTML: riskPane ? riskPane.innerHTML.slice(0, 800) : null,
            zoomMode: document.getElementById('zoom-mode')?.textContent,
            appJs: [...document.scripts].map(s => s.src).filter(s => s.includes('app.js')),
            localStorageMode: localStorage.getItem('cc_map_mode'),
            cityId: localStorage.getItem('cc_city_id'),
            leafletPanes: [...document.querySelectorAll('.leaflet-pane')].map(el => ({
              cls: el.className, kids: el.childElementCount, z: getComputedStyle(el).zIndex
            })),
          };
        }"""
        )

        env_probe = page.evaluate(
            """async () => {
          const token = sessionStorage.getItem('cc_id_token');
          const cityId = localStorage.getItem('cc_city_id') || '11111111-1111-1111-1111-111111111111';
          const r = await fetch('/api/cities/' + cityId + '/environment', {
            headers: { Authorization: 'Bearer ' + token }
          });
          const j = await r.json();
          const feats = j.sources?.features || [];
          const rings = feats.filter(f => f.properties?.showRing);
          return {
            status: r.status,
            districts: (j.districts || []).length,
            features: feats.length,
            rings: rings.length,
            sample: rings[0]?.properties || null,
            coords: rings[0]?.geometry?.coordinates || null,
          };
        }"""
        )

        page.screenshot(path=SHOT, full_page=False)
        payload = {"debug": debug, "envProbe": env_probe, "console": console_logs[-50:]}
        with open(DEBUG, "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=2, ensure_ascii=False)

        print("SHOT", SHOT)
        print(json.dumps({"debug": debug, "envProbe": env_probe}, indent=2, ensure_ascii=False))
        print("--- console ---")
        for c in console_logs[-30:]:
            print(c)
        browser.close()


if __name__ == "__main__":
    main()
