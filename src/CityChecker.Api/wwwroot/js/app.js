import { api, getToken, setToken, clearToken, isTokenExpired } from "./api.js";
import { applyI18n, t, toggleLang } from "./i18n.js";
import { initHousing, housingMapClick, enrichDistrictSheet } from "./housing.js";
import { createRuVoiceInput, isVoiceInputSupported } from "./voice-input.js";

const ZOOM_CITY = 10;
const ZOOM_DISTRICT = 14;
const ZOOM_INTO_DISTRICT = 12;
const LOCKED_MIN_ZOOM = 11;
const CITY_STORAGE_KEY = "cc_city_id";
const MAP_MODE_KEY = "cc_map_mode";
const POLAND_CENTER = [52.1, 19.4];
// Tight mainland Poland frame (Leaflet [lat, lon])
const POLAND_VIEW_BOUNDS = L.latLngBounds([49.05, 14.15], [54.85, 24.15]);
const POLAND_BOUNDS = L.latLngBounds([48.8, 13.8], [55.2, 24.6]);

/** @type {L.Map | null} */
let map = null;
/** @type {L.LayerGroup} */
let cityLayer = L.layerGroup();
/** @type {L.GeoJSON | null} */
let districtLayer = null;
/** @type {L.LayerGroup} */
let buildingLayer = L.layerGroup();
/** @type {L.LayerGroup} */
let pointLayer = L.layerGroup();
/** @type {L.LayerGroup} */
let userLocationLayer = L.layerGroup();
/** @type {HTMLElement | null} */
let userHeadingEl = null;
/** @type {number | null} */
let userHeadingDeg = null;
let userOrientWired = false;
let userOrientGotAbsolute = false;
/** @type {L.LayerGroup} */
let riskSourceLayer = L.layerGroup();

let cities = [];
let activeCityId = null;
/** Locked working city — one at a time */
let lockedCityId = null;
/** @type {string|null} */
let selectedDistrictId = null;
let context = null;
let editingNoteId = null;
/** @type {object | null} */
let pendingMoveNote = null;
/** @type {"comfort"|"environment"} */
let mapMode = localStorage.getItem(MAP_MODE_KEY) === "environment" ? "environment" : "comfort";
const DEFAULT_POINT_RADIUS = 50;
const FAB_DRAG_MIN_PX = 8;
const SHEET_DRAG_THRESHOLD = 40;
const SHEET_SNAP_ORDER = ["peek", "half", "full"];
const isCoarsePointer = () => window.matchMedia("(pointer: coarse)").matches;
/** @type {AbortController | null} */
let mapAbort = null;
/** Bumps when a newer environment load starts — ignore stale responses. */
let envLoadGen = 0;
let moveTimer = null;
/** @type {Record<string, number|null>} */
let districtScores = {};
/** @type {Record<string, number|null>} */
let buildingScores = {};
/** @type {Record<string, number|null>} */
let environmentScores = {};
/** @type {Record<string, object>} */
let environmentDetails = {};

const els = {
  authGate: document.getElementById("auth-gate"),
  app: document.getElementById("app"),
  authError: document.getElementById("auth-error"),
  zoomMode: document.getElementById("zoom-mode"),
  sheet: document.getElementById("sheet"),
  sheetTitle: document.getElementById("sheet-title"),
  sheetMeta: document.getElementById("sheet-meta"),
  notesList: document.getElementById("notes-list"),
  addNoteBtn: document.getElementById("add-note-btn"),
  dialog: document.getElementById("note-dialog"),
  form: document.getElementById("note-form"),
  noteText: document.getElementById("note-text"),
  scoreOverall: document.getElementById("score-overall"),
  scoreOverallOut: document.getElementById("score-overall-out"),
  dialogTitle: document.getElementById("note-dialog-title"),
  noteVoiceBtn: document.getElementById("note-voice-btn"),
  noteVoiceStatus: document.getElementById("note-voice-status"),
  mapModeToggle: document.getElementById("map-mode-toggle"),
};

/** @type {ReturnType<typeof createRuVoiceInput> | null} */
let noteVoice = null;

function resetNoteVoiceUi() {
  els.noteVoiceBtn?.classList.remove("listening");
  els.noteVoiceBtn?.setAttribute("aria-pressed", "false");
  if (els.noteVoiceBtn) {
    els.noteVoiceBtn.setAttribute("title", t("voiceInputStart"));
    els.noteVoiceBtn.setAttribute("aria-label", t("voiceInputStart"));
  }
  if (els.noteVoiceStatus) {
    els.noteVoiceStatus.textContent = "";
    els.noteVoiceStatus.classList.add("hidden");
    els.noteVoiceStatus.classList.remove("is-error");
  }
}

function updateNoteVoiceUi(listening, interim = "") {
  if (!els.noteVoiceBtn) return;
  els.noteVoiceBtn.classList.toggle("listening", listening);
  els.noteVoiceBtn.setAttribute("aria-pressed", listening ? "true" : "false");
  const titleKey = listening ? "voiceInputStop" : "voiceInputStart";
  els.noteVoiceBtn.setAttribute("title", t(titleKey));
  els.noteVoiceBtn.setAttribute("aria-label", t(titleKey));
  if (!els.noteVoiceStatus) return;
  if (listening) {
    els.noteVoiceStatus.classList.remove("hidden", "is-error");
    els.noteVoiceStatus.textContent = interim ? `${t("voiceListening")} ${interim}` : t("voiceListening");
  } else if (!els.noteVoiceStatus.classList.contains("is-error")) {
    els.noteVoiceStatus.textContent = "";
    els.noteVoiceStatus.classList.add("hidden");
  }
}

function stopNoteVoice() {
  noteVoice?.stop();
  resetNoteVoiceUi();
}

if (isVoiceInputSupported() && els.noteText) {
  noteVoice = createRuVoiceInput(els.noteText, {
    onListening: (listening) => updateNoteVoiceUi(listening),
    onInterim: (text) => {
      if (noteVoice?.isListening()) updateNoteVoiceUi(true, text);
    },
    onError: (code) => {
      if (!els.noteVoiceStatus) return;
      els.noteVoiceStatus.classList.remove("hidden");
      els.noteVoiceStatus.classList.add("is-error");
      els.noteVoiceStatus.textContent = code === "not-allowed" ? t("voiceDenied") : t("voiceError");
      els.noteVoiceBtn?.classList.remove("listening");
      els.noteVoiceBtn?.setAttribute("aria-pressed", "false");
      els.noteVoiceBtn?.setAttribute("title", t("voiceInputStart"));
      els.noteVoiceBtn?.setAttribute("aria-label", t("voiceInputStart"));
    },
  });
  els.noteVoiceBtn?.classList.remove("hidden");
  els.noteVoiceBtn?.addEventListener("click", () => noteVoice?.toggle());
}

els.dialog?.addEventListener("close", () => stopNoteVoice());

function scoreColor(score) {
  if (score == null) return "#9aadb6";
  const tNorm = Math.max(0, Math.min(1, (score - 1) / 9));
  const r = Math.round(179 + (13 - 179) * tNorm);
  const g = Math.round(58 + (110 - 58) * tNorm);
  const b = Math.round(58 + (110 - 58) * tNorm);
  return `rgb(${r},${g},${b})`;
}

/** Environment risk 1–10: high = red (inverted comfort scale). */
function riskColor(risk) {
  if (risk == null) return "#9aadb6";
  return scoreColor(11 - risk);
}

function districtFillColor(feature) {
  const id = districtIdOf(feature);
  if (mapMode === "environment") {
    return riskColor(id != null ? environmentScores[id] ?? null : null);
  }
  return scoreColor(feature?.properties?.score);
}

function updateMapModeToggle() {
  const btn = els.mapModeToggle;
  if (!btn) return;
  const key = mapMode === "environment" ? "mapModeEnvironment" : "mapModeComfort";
  btn.textContent = t(key);
  btn.setAttribute("data-i18n", key);
  btn.classList.toggle("map-mode-env", mapMode === "environment");
  btn.title = mapMode === "environment" ? t("mapModeSwitchComfort") : t("mapModeSwitchEnv");
  btn.setAttribute("aria-label", btn.title);
  document.getElementById("env-legend")?.classList.toggle("hidden", mapMode !== "environment");
}

function setMapMode(mode) {
  mapMode = mode === "environment" ? "environment" : "comfort";
  localStorage.setItem(MAP_MODE_KEY, mapMode);
  updateMapModeToggle();
  applyDistrictStyles();
  updateRiskSourceVisibility();
  // ponytail: env load is async and can be aborted by district reload — retry on toggle
  if (mapMode === "environment" && lockedCityId) {
    const cityId = lockedCityId;
    loadEnvironment(cityId).then(() => {
      if (lockedCityId !== cityId) return;
      applyDistrictStyles();
      updateRiskSourceVisibility();
      if (context?.level === "District" && context.districtId) refreshSheet();
    });
  } else if (context?.level === "District") {
    refreshSheet();
  }
}

function ensureRiskPane() {
  if (!map) return;
  if (!map.getPane("risk")) {
    const pane = map.createPane("risk");
    pane.style.zIndex = 650; // above markers (~600) so rings/dots aren't under district UI
  }
  map.getPane("risk").style.pointerEvents = "auto";
}

function updateRiskSourceVisibility() {
  if (!map) return;
  ensureRiskPane();
  const show =
    mapMode === "environment" &&
    lockedCityId &&
    currentMode(map.getZoom()) === "district";
  if (show) {
    if (!map.hasLayer(riskSourceLayer)) riskSourceLayer.addTo(map);
    // ponytail: L.LayerGroup has no bringToFront (throws and aborted env load)
  } else if (map.hasLayer(riskSourceLayer)) {
    map.removeLayer(riskSourceLayer);
  }
}

function loadRiskSources(geojson) {
  riskSourceLayer.clearLayers();
  if (!map) return;
  ensureRiskPane();
  const features = geojson?.features;
  if (!Array.isArray(features) || !features.length) {
    updateRiskSourceVisibility();
    return;
  }

  const colors = {
    landfill: "#e65100",
    waste_incinerator: "#c62828",
    waste_transfer: "#ef6c00",
    factory: "#6a1b9a",
    power_plant: "#4527a0",
    airport: "#1565c0",
  };

  // Prefer curated / high-weight rings so the map stays readable
  const ringFeatures = features
    .filter((f) => {
      const p = f.properties || {};
      const km = Number(p.influenceKm);
      const show = p.showRing === true || p.showRing === "true";
      return show && km > 0 && f.geometry?.coordinates?.length >= 2;
    })
    .sort((a, b) => {
      const ca = a.properties?.curated === true ? 1 : 0;
      const cb = b.properties?.curated === true ? 1 : 0;
      if (cb !== ca) return cb - ca;
      return (Number(b.properties?.weight) || 0) - (Number(a.properties?.weight) || 0);
    })
    .slice(0, 20);

  for (const f of ringFeatures) {
    const p = f.properties || {};
    const coords = f.geometry.coordinates;
    const latlng = L.latLng(coords[1], coords[0]);
    const type = p.type || "landfill";
    const color = colors[type] || "#e65100";
    const meters = Number(p.influenceKm) * 1000;
    // Full circle = rough possible reach (light). Wedge = usual downwind plume.
    L.circle(latlng, {
      pane: "risk",
      radius: meters,
      color,
      weight: 1.5,
      dashArray: "6 8",
      fillColor: color,
      fillOpacity: 0.05,
      opacity: 0.45,
      interactive: false,
      className: "env-influence-ring",
    }).addTo(riskSourceLayer);

    const bearing = Number(p.windBearing);
    if (Number.isFinite(bearing)) {
      L.polygon(windWedgeLatLngs(latlng, meters * 0.95, bearing, 38), {
        pane: "risk",
        color,
        weight: 2,
        fillColor: color,
        fillOpacity: 0.28,
        opacity: 0.75,
        interactive: false,
        className: "env-wind-wedge",
      }).addTo(riskSourceLayer);
    }
  }

  const markerFeatures = {
    type: "FeatureCollection",
    features: features.filter((f) => {
      const typ = f.properties?.type;
      if (typ === "rail") return false;
      if (typ === "factory" || typ === "power_plant") {
        return f.properties?.showRing === true || f.properties?.showRing === "true" || Number(f.properties?.influenceKm) > 0;
      }
      return typ === "landfill" || typ === "waste_incinerator" || typ === "waste_transfer" || typ === "airport";
    }),
  };

  L.geoJSON(markerFeatures, {
    pane: "risk",
    pointToLayer: (feature, latlng) => {
      const type = feature.properties?.type || "landfill";
      const color = colors[type] || "#e65100";
      const r = type === "waste_incinerator" || type === "power_plant" ? 11 : type === "airport" ? 8 : 7;
      return L.circleMarker(latlng, {
        pane: "risk",
        radius: r,
        color: "#fff",
        weight: 2,
        fillColor: color,
        fillOpacity: 1,
        interactive: true,
      });
    },
    onEachFeature: (feature, layer) => {
      const p = feature.properties || {};
      const typeKey = {
        landfill: "envLandfill",
        waste_incinerator: "envIncinerator",
        waste_transfer: "envWasteTransfer",
        factory: "envIndustrial",
        power_plant: "envPowerPlant",
        airport: "envAirport",
      }[p.type] || null;
      const typeLabel = typeKey ? t(typeKey) : p.type || "";
      const lines = [`<strong>${p.name || typeLabel}</strong>`, `<small>${typeLabel}</small>`];
      if (p.influenceKm > 0) {
        lines.push(`<small>${t("envInfluence")}: ~${p.influenceKm} km</small>`);
      }
      if (p.windFrom && p.windTo) {
        lines.push(`<small>${t("envWindPlume")}: ${p.windFrom} → ${p.windTo}</small>`);
      }
      if (p.notes) lines.push(`<small>${p.notes}</small>`);
      if (p.curated === true) lines.push(`<small>${t("envCurated")}</small>`);
      layer.bindPopup(lines.join("<br>"));
    },
  }).addTo(riskSourceLayer);

  updateRiskSourceVisibility();
}

/** Arc wedge in the DOWNWIND / plume direction (bearing = where pollution usually goes). */
function windWedgeLatLngs(center, radiusM, bearingDeg, halfAngleDeg) {
  const pts = [center];
  const start = bearingDeg - halfAngleDeg;
  const end = bearingDeg + halfAngleDeg;
  const steps = 12;
  for (let i = 0; i <= steps; i++) {
    const a = start + ((end - start) * i) / steps;
    pts.push(destinationLatLng(center, radiusM, a));
  }
  return pts;
}

function destinationLatLng(from, distM, bearingDeg) {
  const R = 6371000;
  const δ = distM / R;
  const θ = (bearingDeg * Math.PI) / 180;
  const φ1 = (from.lat * Math.PI) / 180;
  const λ1 = (from.lng * Math.PI) / 180;
  const φ2 = Math.asin(Math.sin(φ1) * Math.cos(δ) + Math.cos(φ1) * Math.sin(δ) * Math.cos(θ));
  const λ2 =
    λ1 +
    Math.atan2(Math.sin(θ) * Math.sin(δ) * Math.cos(φ1), Math.cos(δ) - Math.sin(φ1) * Math.sin(φ2));
  return L.latLng((φ2 * 180) / Math.PI, (λ2 * 180) / Math.PI);
}

async function loadEnvironment(cityId) {
  // ponytail: do not share mapAbort — district reload was wiping env mid-fetch
  const gen = ++envLoadGen;
  try {
    const env = await api(`/api/cities/${cityId}/environment`);
    if (gen !== envLoadGen || lockedCityId !== cityId) return;
    const nextScores = {};
    const nextDetails = {};
    for (const d of env.districts || []) {
      const id = d.districtId != null ? String(d.districtId) : null;
      if (!id) continue;
      nextScores[id] = d.envRiskOverall;
      nextDetails[id] = d;
    }
    environmentScores = nextScores;
    environmentDetails = nextDetails;
    try {
      loadRiskSources(env.sources);
    } catch (ringErr) {
      console.warn("risk sources failed", ringErr);
    }
    applyDistrictStyles();
    updateRiskSourceVisibility();
  } catch (e) {
    if (e?.name === "AbortError") return;
    if (gen !== envLoadGen) return;
    console.warn("environment load failed", e);
  }
}

function formatEnvMeta(districtId) {
  if (districtId == null) return mapMode === "environment" ? t("envNoData") : "";
  const d = environmentDetails[String(districtId)];
  if (!d) {
    if (mapMode !== "environment") return "";
    return Object.keys(environmentDetails).length ? t("envNoData") : t("loading");
  }
  const parts = [`${t("envRisk")}: ${d.envRiskOverall}/10`];
  if (d.nearestLandfillKm != null) {
    let line = `${t("envLandfill")} ${d.nearestLandfillKm} km`;
    if (d.landfillDownwind) line += ` (${t("envLandfillDownwind")})`;
    parts.push(line);
  }
  if (d.nearestRailKm != null) parts.push(`${t("envRail")} ${d.nearestRailKm} km`);
  if (d.nearestAirportKm != null) parts.push(`${t("envAirport")} ${d.nearestAirportKm} km`);
  if (d.nearestIndustrialKm != null) parts.push(`${t("envIndustrial")} ${d.nearestIndustrialKm} km`);
  if (d.nearestHighwayKm != null) parts.push(`${t("envHighway")} ${d.nearestHighwayKm} km`);
  return parts.join(" · ");
}

function currentMode(zoom) {
  if (zoom <= ZOOM_CITY) return "city";
  if (zoom <= ZOOM_DISTRICT) return "district";
  return "building";
}

function isMobileSheet() {
  return window.matchMedia("(max-width: 899px)").matches;
}

function getSheetSnap() {
  if (els.sheet.classList.contains("sheet-peek")) return "peek";
  if (els.sheet.classList.contains("sheet-full")) return "full";
  return "half";
}

function setSheetSnap(snap) {
  if (!isMobileSheet()) {
    els.sheet.classList.remove("sheet-peek", "sheet-half", "sheet-full");
    return;
  }
  els.sheet.classList.remove("sheet-peek", "sheet-half", "sheet-full");
  els.sheet.classList.add(`sheet-${snap}`);
  updateFabPosition();
  updateSheetHandleAria();
}

function updateSheetHandleAria() {
  const handle = document.getElementById("sheet-handle");
  if (!handle || !isMobileSheet()) return;
  const key = getSheetSnap() === "full" ? "sheetCollapse" : "sheetExpand";
  handle.setAttribute("aria-label", t(key));
}

function updateFabPosition() {
  const stack = document.getElementById("map-fabs");
  if (!stack) return;
  if (!isMobileSheet()) {
    stack.style.bottom = "";
    return;
  }
  const sheetH = els.sheet.getBoundingClientRect().height;
  const safe =
    parseInt(getComputedStyle(document.documentElement).getPropertyValue("env(safe-area-inset-bottom)")) || 0;
  stack.style.bottom = `${sheetH + 12 + safe}px`;
}

function cycleSheetSnap() {
  const order = ["half", "full", "peek"];
  const i = order.indexOf(getSheetSnap());
  setSheetSnap(order[(i + 1) % order.length]);
}

function initSheet() {
  if (els.sheet.dataset.wired) return;
  els.sheet.dataset.wired = "1";
  setSheetSnap("half");
  const ro = new ResizeObserver(() => updateFabPosition());
  ro.observe(els.sheet);
  window.addEventListener("resize", updateFabPosition);
  initSheetHandle();
  els.dialog.addEventListener("close", () => {
    if (lockedCityId) setPlaceNoteFabVisible(true);
    updateFabPosition();
  });
}

function initSheetHandle() {
  const handle = document.getElementById("sheet-handle");
  if (!handle || handle.dataset.wired) return;
  handle.dataset.wired = "1";

  let dragging = false;
  let moved = false;
  let startY = 0;
  let dragY = 0;

  handle.addEventListener("pointerdown", (e) => {
    if (!isMobileSheet()) return;
    dragging = true;
    moved = false;
    dragY = 0;
    startY = e.clientY;
    handle.setPointerCapture(e.pointerId);
  });

  handle.addEventListener("pointermove", (e) => {
    if (!dragging) return;
    dragY = e.clientY - startY;
    if (Math.abs(dragY) >= SHEET_DRAG_THRESHOLD) moved = true;
  });

  const finish = (e) => {
    if (!dragging) return;
    dragging = false;
    try {
      handle.releasePointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    if (!isMobileSheet()) return;
    if (moved) {
      const cur = SHEET_SNAP_ORDER.indexOf(getSheetSnap());
      if (dragY < -SHEET_DRAG_THRESHOLD && cur < SHEET_SNAP_ORDER.length - 1) {
        setSheetSnap(SHEET_SNAP_ORDER[cur + 1]);
      } else if (dragY > SHEET_DRAG_THRESHOLD && cur > 0) {
        setSheetSnap(SHEET_SNAP_ORDER[cur - 1]);
      }
      return;
    }
    cycleSheetSnap();
  };

  handle.addEventListener("pointerup", finish);
  handle.addEventListener("pointercancel", finish);
}

function lockedCityName() {
  const c = cities.find((x) => x.cityId === lockedCityId);
  return c?.name ?? t("selectPlace");
}

function setPlaceNoteFabVisible(visible) {
  const fab = document.getElementById("place-note-fab");
  if (!fab) return;
  fab.classList.toggle("hidden", !visible);
  if (visible) {
    updatePlaceNoteFabLabel();
    updateFabPosition();
  }
}

function updatePlaceNoteFabLabel() {
  const fab = document.getElementById("place-note-fab");
  if (!fab) return;
  const label = t("placeNoteFab");
  fab.setAttribute("aria-label", label);
  fab.setAttribute("title", label);
}

async function clearSelection() {
  if (!lockedCityId) return;
  cancelPointMove();
  selectedDistrictId = null;
  applyDistrictStyles();
  context = { level: "City", cityId: lockedCityId, title: lockedCityName() };
  document.getElementById("housing-district-slot").innerHTML = "";
  setSheetSnap("peek");
  await refreshSheet();
}

function initPlaceNoteFab() {
  const fab = document.getElementById("place-note-fab");
  const ghost = document.getElementById("place-note-ghost");
  const mapEl = document.getElementById("map");
  if (!fab || !ghost || !mapEl || fab.dataset.wired) return;
  fab.dataset.wired = "1";
  updatePlaceNoteFabLabel();

  let dragging = false;
  let startX = 0;
  let startY = 0;
  let moved = false;

  const moveGhost = (clientX, clientY) => {
    ghost.style.left = `${clientX}px`;
    ghost.style.top = `${clientY}px`;
  };

  fab.addEventListener("pointerdown", (e) => {
    if (!lockedCityId) return;
    e.preventDefault();
    dragging = true;
    moved = false;
    startX = e.clientX;
    startY = e.clientY;
    fab.setPointerCapture(e.pointerId);
    ghost.classList.remove("hidden");
    document.body.classList.add("place-note-dragging");
    moveGhost(e.clientX, e.clientY);
  });

  fab.addEventListener("pointermove", (e) => {
    if (!dragging) return;
    e.preventDefault();
    if (Math.hypot(e.clientX - startX, e.clientY - startY) >= FAB_DRAG_MIN_PX) moved = true;
    moveGhost(e.clientX, e.clientY);
  });

  const finishDrag = async (e) => {
    if (!dragging) return;
    dragging = false;
    ghost.classList.add("hidden");
    document.body.classList.remove("place-note-dragging");
    try {
      fab.releasePointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    if (!moved || !lockedCityId || !map) return;

    const mapContainer = map.getContainer();
    const { clientX: x, clientY: y } = e;
    const hit = document.elementFromPoint(x, y);
    if (!hit || !mapContainer.contains(hit)) return;

    const latlng = map.mouseEventToLatLng(e);
    await placeNewPointNote(latlng);
  };

  fab.addEventListener("pointerup", finishDrag);
  fab.addEventListener("pointercancel", finishDrag);
}

function setLocateBusy(busy) {
  const btn = document.getElementById("locate-me-btn");
  if (!btn) return;
  btn.disabled = busy;
  btn.setAttribute("aria-busy", busy ? "true" : "false");
}

function compassHeadingFromEvent(e) {
  if (typeof e.webkitCompassHeading === "number" && !Number.isNaN(e.webkitCompassHeading)) {
    return e.webkitCompassHeading;
  }
  if (typeof e.alpha !== "number" || Number.isNaN(e.alpha)) return null;
  const screenAngle = screen.orientation?.angle ?? window.orientation ?? 0;
  let heading = (360 - e.alpha + Number(screenAngle || 0)) % 360;
  if (heading < 0) heading += 360;
  return heading;
}

function applyUserHeading() {
  if (!userHeadingEl || userHeadingDeg == null) return;
  userHeadingEl.style.transform = `rotate(${userHeadingDeg}deg)`;
}

function onUserOrientation(e) {
  const heading = compassHeadingFromEvent(e);
  if (heading == null) return;
  if (e.type === "deviceorientationabsolute") userOrientGotAbsolute = true;
  else if (userOrientGotAbsolute) return;
  userHeadingDeg = heading;
  applyUserHeading();
}

function startUserOrientation() {
  if (userOrientWired) return;
  userOrientWired = true;
  window.addEventListener("deviceorientationabsolute", onUserOrientation, true);
  window.addEventListener("deviceorientation", onUserOrientation, true);
}

async function ensureOrientationPermission() {
  for (const Ev of [window.DeviceOrientationEvent, window.DeviceMotionEvent]) {
    if (Ev && typeof Ev.requestPermission === "function") {
      try {
        await Ev.requestPermission();
      } catch {
        /* iOS may deny */
      }
    }
  }
  startUserOrientation();
}

function bindUserHeadingEl(marker) {
  const hook = () => {
    userHeadingEl = marker.getElement()?.querySelector(".user-heading-rot") ?? null;
    applyUserHeading();
  };
  hook();
  if (!userHeadingEl) requestAnimationFrame(hook);
}

function initLocateMe() {
  const btn = document.getElementById("locate-me-btn");
  if (!btn || !map || btn.dataset.wired) return;
  btn.dataset.wired = "1";

  map.on("locationfound", (e) => {
    setLocateBusy(false);
    userLocationLayer.clearLayers();
    userHeadingEl = null;
    if (typeof e.heading === "number" && e.heading >= 0 && userHeadingDeg == null) {
      userHeadingDeg = e.heading;
    }
    L.circle(e.latlng, {
      radius: e.accuracy,
      color: "#4285F4",
      weight: 1,
      fillColor: "#4285F4",
      fillOpacity: 0.15,
      interactive: false,
    }).addTo(userLocationLayer);
    const marker = L.marker(e.latlng, {
      interactive: false,
      keyboard: false,
      zIndexOffset: 1200,
      icon: L.divIcon({
        className: "user-heading-icon",
        iconSize: [96, 96],
        iconAnchor: [48, 48],
        html: '<div class="user-heading-rot"><div class="user-heading-cone" aria-hidden="true"></div><div class="user-heading-dot"></div></div>',
      }),
    }).addTo(userLocationLayer);
    bindUserHeadingEl(marker);
  });

  map.on("locationerror", () => {
    setLocateBusy(false);
    alert(t("locateFail"));
  });

  btn.addEventListener("click", async () => {
    if (!map || btn.disabled) return;
    setLocateBusy(true);
    await ensureOrientationPermission();
    map.stopLocate();
    map.locate({
      setView: true,
      maxZoom: 16,
      enableHighAccuracy: true,
      timeout: 15000,
      maximumAge: 8000,
    });
  });
}

function updateZoomLabel() {
  if (!map) return;
  const mode = currentMode(map.getZoom());
  const key = mode === "city" ? "modeCity" : mode === "district" ? "modeDistrict" : "modeBuilding";
  els.zoomMode.textContent = t(key);
}

function requireAuthOrGate() {
  const token = getToken();
  if (!token || isTokenExpired(token)) {
    clearToken();
    return false;
  }
  return true;
}

function scrubLeakedAuthQuery() {
  // Login form used to GET-submit before JS was ready → ?email=&password= in the URL.
  if (/[?&](email|password)=/i.test(location.search)) {
    history.replaceState(null, "", location.pathname + location.hash);
  }
}

async function initAuth() {
  applyI18n();
  scrubLeakedAuthQuery();

  const form = document.getElementById("password-form");
  const tabSignIn = document.getElementById("tab-signin");
  const tabSignUp = document.getElementById("tab-signup");
  const submitBtn = document.getElementById("auth-submit");
  const passwordInput = document.getElementById("login-password");
  let mode = "signin";

  function setMode(next) {
    mode = next;
    tabSignIn.classList.toggle("active", mode === "signin");
    tabSignUp.classList.toggle("active", mode === "signup");
    submitBtn.textContent = t(mode === "signin" ? "signIn" : "signUp");
    passwordInput.autocomplete = mode === "signin" ? "current-password" : "new-password";
    els.authError.classList.add("hidden");
  }
  tabSignIn.onclick = () => setMode("signin");
  tabSignUp.onclick = () => setMode("signup");

  // Wire password form immediately — do not wait for Google GIS (that caused GET submits).
  form.onsubmit = async (e) => {
    e.preventDefault();
    e.stopPropagation();
    els.authError.classList.add("hidden");
    const path = mode === "signup" ? "/api/auth/register" : "/api/auth/login";
    try {
      const res = await fetch(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          email: document.getElementById("login-email").value,
          password: passwordInput.value,
        }),
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) throw { message: body.error || t("authFailed"), status: res.status, body };
      setToken(body.token);
      sessionStorage.setItem("cc_had_token", "1");
      history.replaceState(null, "", location.pathname);
      await api("/api/cities");
      showApp();
    } catch (err) {
      showAuthError(err);
      clearToken();
    }
  };

  if (requireAuthOrGate()) {
    try {
      await api("/api/cities");
      showApp();
      return;
    } catch (err) {
      if (err.status === 401 || err.status === 403) {
        showAuthError(err.status === 401 ? { message: t("sessionExpired") } : err);
      } else {
        showAuthError(err);
      }
      clearToken();
    }
  } else if (getToken() === null && sessionStorage.getItem("cc_had_token")) {
    els.authError.textContent = t("sessionExpired");
    els.authError.classList.remove("hidden");
  }

  const cfg = await fetch("/api/config").then((r) => r.json()).catch(() => ({}));
  wireGoogle(cfg.googleClientId);
}

function wireGoogle(clientId) {
  const tryInit = () => {
    const canGoogle = clientId && !String(clientId).includes("YOUR_GOOGLE") && window.google?.accounts?.id;
    if (!canGoogle) {
      if (!window.google?.accounts?.id) setTimeout(tryInit, 50);
      return;
    }
    const orEl = document.getElementById("auth-or");
    if (orEl) orEl.classList.remove("hidden");
    window.google.accounts.id.initialize({
      client_id: clientId,
      callback: async (response) => {
        setToken(response.credential);
        sessionStorage.setItem("cc_had_token", "1");
        try {
          await api("/api/cities");
          showApp();
        } catch (err) {
          showAuthError(err);
          clearToken();
        }
      },
    });
    window.google.accounts.id.renderButton(document.getElementById("google-btn"), {
      theme: "outline",
      size: "large",
      width: 280,
    });
  };
  tryInit();
}

function bootAuth() {
  scrubLeakedAuthQuery();
  // Block native navigation even before initAuth finishes fetching config.
  document.getElementById("password-form")?.addEventListener("submit", (e) => {
    e.preventDefault();
  });
  initAuth();
}
bootAuth();

function showAuthError(err) {
  els.authGate.classList.remove("hidden");
  els.app.classList.add("hidden");
  const sub = err?.body?.yourGoogleSub;
  if (sub) {
    els.authError.innerHTML = `${t("authFailed")}<br><br>${t("authSubHint")}<br><code style="user-select:all;word-break:break-all">${sub}</code>`;
  } else {
    els.authError.textContent = err?.message || t("authFailed");
  }
  els.authError.classList.remove("hidden");
}

async function showApp() {
  els.authGate.classList.add("hidden");
  els.app.classList.remove("hidden");
  els.authError.classList.add("hidden");
  applyI18n();
  initMap();
  initSheet();
  cities = await api("/api/cities");
  wireCityUi();
  updateZoomLabel();

  requestAnimationFrame(() => {
    requestAnimationFrame(() => map?.invalidateSize());
  });

  const available = availableCities();
  const saved = loadSavedCityId();
  const savedCity = available.find((c) => c.cityId === saved);
  if (savedCity) {
    hideCityPicker();
    await enterCity(savedCity, { persist: false });
  } else {
    if (saved) localStorage.removeItem(CITY_STORAGE_KEY);
    showCityPicker();
  }
}

function availableCities() {
  return cities.filter((c) => (c.districtCount ?? 0) > 0);
}

function loadSavedCityId() {
  return localStorage.getItem(CITY_STORAGE_KEY);
}

function saveCityId(cityId) {
  localStorage.setItem(CITY_STORAGE_KEY, cityId);
}

function showCityPicker() {
  const overlay = document.getElementById("city-picker");
  const list = document.getElementById("city-picker-list");
  if (!overlay || !list) return;
  fillCityList(list, null);
  overlay.classList.remove("hidden");
}

function hideCityPicker() {
  document.getElementById("city-picker")?.classList.add("hidden");
}

function fillCityList(listEl, highlightId) {
  listEl.innerHTML = "";
  for (const c of availableCities()) {
    const li = document.createElement("li");
    const btn = document.createElement("button");
    btn.type = "button";
    if (highlightId && c.cityId === highlightId) btn.classList.add("active");
    btn.innerHTML = `${c.name}<span class="meta">${c.voivodeship || ""} · ${c.districtCount}</span>`;
    btn.onclick = () => onCityChosen(c);
    li.appendChild(btn);
    listEl.appendChild(li);
  }
}

function wireCityUi() {
  const tab = document.getElementById("city-drawer-tab");
  const drawer = document.getElementById("city-drawer");
  const scrim = document.getElementById("city-drawer-scrim");
  if (!tab || tab.dataset.wired) return;
  tab.dataset.wired = "1";

  const close = () => {
    drawer?.classList.remove("open");
    scrim?.classList.add("hidden");
    tab.setAttribute("aria-expanded", "false");
    drawer?.setAttribute("aria-hidden", "true");
  };
  const open = () => {
    fillCityList(document.getElementById("city-drawer-list"), lockedCityId);
    drawer?.classList.add("open");
    scrim?.classList.remove("hidden");
    tab.setAttribute("aria-expanded", "true");
    drawer?.setAttribute("aria-hidden", "false");
  };

  tab.addEventListener("click", () => {
    if (drawer?.classList.contains("open")) close();
    else open();
  });
  scrim?.addEventListener("click", close);
}

function updateCityTabLabel(city) {
  const label = document.getElementById("city-drawer-tab-label");
  if (!label) return;
  label.textContent = city?.name || t("citiesTab");
}

async function onCityChosen(city) {
  await enterCity(city, { persist: true });
}

function closeCityDrawer() {
  document.getElementById("city-drawer")?.classList.remove("open");
  document.getElementById("city-drawer-scrim")?.classList.add("hidden");
  document.getElementById("city-drawer-tab")?.setAttribute("aria-expanded", "false");
  document.getElementById("city-drawer")?.setAttribute("aria-hidden", "true");
}

async function enterCity(city, { persist = true } = {}) {
  if (!map || !city) return;
  if (persist) saveCityId(city.cityId);

  const lat = Number(city.centerLat);
  const lon = Number(city.centerLon);
  if (!Number.isFinite(lat) || !Number.isFinite(lon)) {
    console.error("enterCity: invalid center", city);
    return;
  }

  lockedCityId = city.cityId;
  selectedDistrictId = null;
  mapShortlistIds = null;
  cancelPointMove();
  buildingLayer.clearLayers();
  userLocationLayer.clearLayers();
  userHeadingEl = null;
  setPlaceNoteFabVisible(true);
  if (map.hasLayer(cityLayer)) map.removeLayer(cityLayer);

  hideCityPicker();
  closeCityDrawer();

  // Move to the city FIRST, then raise minZoom. Raising minZoom while still on
  // Poland-center would clamp zoom to 11 over the wrong place.
  map.setMinZoom(6);
  map.setView([lat, lon], ZOOM_INTO_DISTRICT, { animate: false });
  map.setMinZoom(LOCKED_MIN_ZOOM);

  context = { level: "City", cityId: city.cityId, title: city.name };
  document.getElementById("housing-district-slot").innerHTML = "";
  updateCityTabLabel(city);
  updateZoomLabel();

  await loadDistricts(city.cityId);
  applyDistrictStyles();
  await loadPointNotes();
  setPlaceNoteFabVisible(true);
  updatePlaceNoteFabLabel();

  // Picker overlay was covering the map — size/center can be wrong until layout settles.
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  map.invalidateSize();
  map.setView([lat, lon], ZOOM_INTO_DISTRICT, { animate: false });

  await refreshSheet();
  setSheetSnap("half");
  updateFabPosition();
}

function fitPolandView() {
  if (!map) return;
  map.invalidateSize();
  map.fitBounds(POLAND_VIEW_BOUNDS, { padding: [16, 16], maxZoom: 8, animate: false });
}

function initMap() {
  if (map) return;
  map = L.map("map", {
    center: POLAND_CENTER,
    zoom: 7,
    maxBounds: POLAND_BOUNDS.pad(0.15),
    minZoom: 6,
    zoomControl: true,
  });
  L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
  }).addTo(map);

  ensureRiskPane();

  cityLayer.addTo(map);
  buildingLayer.addTo(map);
  pointLayer.addTo(map);
  userLocationLayer.addTo(map);

  map.on("zoomend", scheduleMapUpdate);
  map.on("moveend", scheduleMapUpdate);
  map.on("click", onMapClick);

  updateMapModeToggle();
  els.mapModeToggle?.addEventListener("click", () => {
    setMapMode(mapMode === "environment" ? "comfort" : "environment");
  });

  initPlaceNoteFab();
  initLocateMe();
  initHousing({
    map,
    getActiveCityId: () => activeCityId,
    getContext: () => context,
    onMapFilterChange: (ids) => {
      mapShortlistIds = ids ? new Set(ids.map(String)) : null;
      applyDistrictStyles();
    },
  });
}

function scheduleMapUpdate() {
  clearTimeout(moveTimer);
  moveTimer = setTimeout(() => onZoomOrMove(), 300);
}

function renderCityMarkers() {
  cityLayer.clearLayers();
  // Hide cities with no district polygons (Kraków/Warszawa until imported)
  for (const c of cities.filter((x) => (x.districtCount ?? 0) > 0)) {
    const m = L.circleMarker([c.centerLat, c.centerLon], {
      radius: 8,
      color: "#0d6e6e",
      fillColor: "#0d6e6e",
      fillOpacity: 0.85,
      weight: 2,
    });
    m.bindTooltip(c.name, { permanent: false, direction: "top" });
    m.on("click", (e) => {
      L.DomEvent.stopPropagation(e);
      selectCity(c);
    });
    cityLayer.addLayer(m);
  }
}

async function onZoomOrMove() {
  if (!requireAuthOrGate()) {
    showAuthError({ message: t("sessionExpired") });
    return;
  }
  updateZoomLabel();
  if (!lockedCityId) return;

  const mode = currentMode(map.getZoom());
  // Locked to one city — never clear districts or show Poland city markers
  if (map.hasLayer(cityLayer)) map.removeLayer(cityLayer);

  if (activeCityId !== lockedCityId || !districtLayer) {
    await loadDistricts(lockedCityId);
  }

  setDistrictInteractive(mode === "district" || mode === "city");
  updateRiskSourceVisibility();
  if (pendingMoveNote) return;
  if (mode === "building") {
    await loadBuildingMarkers();
    await loadPointNotes();
  } else {
    buildingLayer.clearLayers();
    await loadPointNotes();
  }
}

function setDistrictInteractive(interactive) {
  const on = interactive && !pendingMoveNote;
  applyDistrictStyles(on);
  if (!districtLayer) return;
  districtLayer.eachLayer((layer) => {
    const el = layer.getElement?.() || layer._path;
    if (el) el.style.pointerEvents = on ? "auto" : "none";
  });
}

function districtIdOf(feature) {
  return feature?.properties?.districtId || feature?.properties?.id || null;
}

/** @type {Set<string>|null} */
let mapShortlistIds = null;

function districtBaseStyle(feature, interactive) {
  const id = districtIdOf(feature);
  const selected = selectedDistrictId != null && id === selectedDistrictId;
  const dimOthers = selectedDistrictId != null && !selected;
  const filteredOut = mapShortlistIds != null && id != null && !mapShortlistIds.has(String(id));
  const fill = districtFillColor(feature);

  if (filteredOut) {
    return {
      color: "#b0bec5",
      weight: 0.8,
      fillColor: fill,
      fillOpacity: 0.06,
      opacity: 0.25,
      lineJoin: "round",
      className: "district-poly district-filtered-out",
    };
  }

  if (selected) {
    return {
      color: "#0a5c5c",
      weight: 3.5,
      fillColor: fill,
      fillOpacity: 0.62,
      opacity: 1,
      lineJoin: "round",
      lineCap: "round",
      className: "district-poly district-selected",
    };
  }

  return {
    color: dimOthers ? "#7a8f99" : "#1a2b33",
    weight: interactive ? 1.4 : 1,
    fillColor: fill,
    fillOpacity: interactive ? (dimOthers ? 0.2 : 0.42) : 0.14,
    opacity: dimOthers ? 0.5 : 0.9,
    lineJoin: "round",
    lineCap: "round",
    className: "district-poly",
  };
}

function applyDistrictStyles(interactive = currentMode(map?.getZoom?.() ?? 0) === "district") {
  if (!districtLayer) return;
  districtLayer.eachLayer((layer) => {
    const el = layer.getElement?.() || layer._path;
    if (el) el.style.pointerEvents = interactive ? "auto" : "none";
    layer.setStyle(districtBaseStyle(layer.feature, interactive));
    if (districtIdOf(layer.feature) === selectedDistrictId) layer.bringToFront();
  });
}

function nearestCity(latlng) {
  let best = null;
  let bestD = Infinity;
  for (const c of cities) {
    const d = map.distance(latlng, [c.centerLat, c.centerLon]);
    if (d < bestD) {
      bestD = d;
      best = c;
    }
  }
  return bestD < 80000 ? best : null;
}

function clearDistricts() {
  if (districtLayer) {
    map.removeLayer(districtLayer);
    districtLayer = null;
  }
  riskSourceLayer.clearLayers();
  if (map?.hasLayer(riskSourceLayer)) map.removeLayer(riskSourceLayer);
  pointLayer.clearLayers();
  activeCityId = null;
  selectedDistrictId = null;
  districtScores = {};
  buildingScores = {};
  environmentScores = {};
  environmentDetails = {};
  setPlaceNoteFabVisible(false);
}

async function refreshCityAggregates(cityId, signal) {
  const batch = await api(`/api/cities/${cityId}/aggregates`, { signal });
  districtScores = {};
  buildingScores = {};
  for (const d of batch.districts || []) districtScores[d.id] = d.scoreOverall;
  for (const b of batch.buildings || []) buildingScores[b.id] = b.scoreOverall;
  return batch;
}

async function loadDistricts(cityId) {
  if (mapAbort) mapAbort.abort();
  mapAbort = new AbortController();
  const { signal } = mapAbort;

  if (districtLayer) {
    map.removeLayer(districtLayer);
    districtLayer = null;
  }
  activeCityId = cityId;

  const token = getToken();
  const [res] = await Promise.all([
    fetch(`/api/cities/${cityId}/districts/geojson`, {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
      signal,
    }),
    refreshCityAggregates(cityId, signal),
  ]);
  if (!res.ok) throw new Error("Failed to load district GeoJSON");
  const fc = await res.json();

  for (const f of fc.features || []) {
    const id = f.properties?.id;
    f.properties.districtId = id;
    f.properties.cityId = f.properties.cityId || cityId;
    f.properties.score = id ? districtScores[id] ?? null : null;
  }

  districtLayer = L.geoJSON(fc, {
    style: (f) => districtBaseStyle(f, currentMode(map.getZoom()) === "district"),
    onEachFeature: (feature, layer) => {
      layer.on("click", (e) => {
        if (!lockedCityId) return;
        L.DomEvent.stopPropagation(e);
        if (pendingMoveNote) {
          commitPointMove(pendingMoveNote, e.latlng);
          return;
        }
        selectDistrict(feature.properties);
      });
    },
  }).addTo(map);

  applyDistrictStyles();

  // Environment may hit Overpass on first load (~10–60s); don't block district paint.
  // Uses its own gen-counter (not mapAbort) so moveend/district reload can't wipe it.
  loadEnvironment(cityId).then(() => {
    if (activeCityId !== cityId) return;
    applyDistrictStyles();
    updateRiskSourceVisibility();
    if (context?.level === "District" && context.districtId) refreshSheet();
  });
  // Do not fitBounds here: on a short mobile viewport it can zoom out past ZOOM_CITY,
  // clear districts, reload, and loop. City tap already setView()'d into the band.
}

async function loadPointNotes() {
  if (!lockedCityId && !activeCityId) return;
  const cityId = lockedCityId || activeCityId;
  try {
    const notes = await api(`/api/notes?cityId=${cityId}&level=Point`);
    pointLayer.clearLayers();
    const dotRadius = isCoarsePointer() ? 8 : 5;
    for (const n of notes) {
      if (n.lat == null || n.lon == null) continue;
      const color = scoreColor(n.scoreOverall);
      const radius = n.radiusMeters || DEFAULT_POINT_RADIUS;
      const circle = L.circle([n.lat, n.lon], {
        radius,
        color,
        fillColor: color,
        fillOpacity: 0.25,
        weight: 1.5,
        opacity: 0.85,
        interactive: false,
      });
      pointLayer.addLayer(circle);
      const dot = L.circleMarker([n.lat, n.lon], {
        radius: dotRadius,
        color: "#1a2b33",
        fillColor: color,
        fillOpacity: 1,
        weight: 1,
      });
      dot.on("click", (e) => {
        L.DomEvent.stopPropagation(e);
        if (pendingMoveNote) {
          commitPointMove(pendingMoveNote, e.latlng);
          return;
        }
        selectPointNote(n);
      });
      pointLayer.addLayer(dot);
    }
  } catch (err) {
    if (err.status === 401) showAuthError({ message: t("sessionExpired") });
  }
}

function pointNoteWriteBody(n, latlng) {
  return {
    level: "Point",
    targetCityId: n.targetCityId,
    targetDistrictId: n.targetDistrictId ?? null,
    targetBuildingId: null,
    text: n.text,
    scoreOverall: n.scoreOverall,
    scoreNature: n.scoreNature ?? null,
    scoreShops: n.scoreShops ?? null,
    scoreTransport: n.scoreTransport ?? null,
    scoreSafety: n.scoreSafety ?? null,
    lat: latlng.lat,
    lon: latlng.lng,
    radiusMeters: n.radiusMeters ?? DEFAULT_POINT_RADIUS,
  };
}

function setMoveModeUi(on) {
  map?.getContainer()?.classList.toggle("moving-point", on);
  const mode = map ? currentMode(map.getZoom()) : "district";
  setDistrictInteractive(mode === "district" || mode === "city");
}

function startPointMove(n) {
  if (pendingMoveNote && pendingMoveNote.noteId === n.noteId) {
    cancelPointMove();
    refreshSheet();
    return;
  }
  pendingMoveNote = n;
  setMoveModeUi(true);
  refreshSheet();
}

function cancelPointMove() {
  if (!pendingMoveNote) return;
  pendingMoveNote = null;
  setMoveModeUi(false);
}

async function commitPointMove(n, latlng) {
  pendingMoveNote = null;
  setMoveModeUi(false);
  try {
    const saved = await api(`/api/notes/${n.noteId}`, {
      method: "PUT",
      body: JSON.stringify(pointNoteWriteBody(n, latlng)),
    });
    await reloadDistrictColors();
    await loadPointNotes();
    if (saved) await selectPointNote(saved);
    else await refreshSheet();
  } catch (err) {
    if (err.status === 401) showAuthError({ message: t("sessionExpired") });
    else await loadPointNotes();
  }
}

async function selectPointNote(n) {
  context = {
    level: "Point",
    cityId: n.targetCityId || lockedCityId || activeCityId,
    districtId: n.targetDistrictId ?? null,
    lat: n.lat,
    lon: n.lon,
    radiusMeters: n.radiusMeters || DEFAULT_POINT_RADIUS,
    title: t("pointNote"),
  };
  document.getElementById("housing-district-slot").innerHTML = "";
  if (n.targetDistrictId) {
    selectedDistrictId = n.targetDistrictId;
    applyDistrictStyles();
    await enrichDistrictSheet(n.targetDistrictId, document.getElementById("housing-district-slot"));
  } else {
    selectedDistrictId = null;
    applyDistrictStyles();
  }
  setSheetSnap("half");
  await refreshSheet();
}

async function placeNewPointNote(latlng) {
  selectedDistrictId = null;
  applyDistrictStyles();
  context = {
    level: "Point",
    cityId: lockedCityId || activeCityId,
    districtId: null,
    lat: latlng.lat,
    lon: latlng.lng,
    radiusMeters: DEFAULT_POINT_RADIUS,
    title: `${latlng.lat.toFixed(5)}, ${latlng.lng.toFixed(5)}`,
  };
  document.getElementById("housing-district-slot").innerHTML = "";
  openNoteForm();
}

async function reloadDistrictColors() {
  if (!activeCityId || !districtLayer) return;
  await refreshCityAggregates(activeCityId);
  districtLayer.eachLayer((layer) => {
    const id = layer.feature?.properties?.id;
    if (id) layer.feature.properties.score = districtScores[id] ?? null;
  });
  applyDistrictStyles();
}

async function loadBuildingMarkers() {
  if (!activeCityId) return;
  if (mapAbort) {
    /* keep previous abort for districts; use separate signal for buildings */
  }
  const ctrl = new AbortController();
  const b = map.getBounds();
  const qs = new URLSearchParams({
    minLat: String(b.getSouth()),
    minLon: String(b.getWest()),
    maxLat: String(b.getNorth()),
    maxLon: String(b.getEast()),
  });
  if (!Object.keys(buildingScores).length) {
    try {
      await refreshCityAggregates(activeCityId, ctrl.signal);
    } catch { /* ignore */ }
  }
  const buildings = await api(`/api/cities/${activeCityId}/buildings?${qs}`, { signal: ctrl.signal });
  buildingLayer.clearLayers();
  for (const bld of buildings) {
    const score = buildingScores[bld.buildingId] ?? null;
    const m = L.circleMarker([bld.lat, bld.lon], {
      radius: 6,
      color: scoreColor(score),
      fillColor: scoreColor(score),
      fillOpacity: 0.9,
      weight: 1,
    });
    m.bindTooltip(bld.addressLine);
    m.on("click", (e) => {
      L.DomEvent.stopPropagation(e);
      if (pendingMoveNote) {
        commitPointMove(pendingMoveNote, e.latlng);
        return;
      }
      selectBuilding(bld);
    });
    buildingLayer.addLayer(m);
  }
}

async function onMapClick(e) {
  if (!requireAuthOrGate()) {
    showAuthError({ message: t("sessionExpired") });
    return;
  }
  if (pendingMoveNote) {
    await commitPointMove(pendingMoveNote, e.latlng);
    return;
  }
  if (housingMapClick(e.latlng)) return;
  const mode = currentMode(map.getZoom());
  // Shift+click empty map (building zoom): reverse-geocode building. Plain tap: clear selection.
  if (lockedCityId && e.originalEvent?.shiftKey && mode === "building") {
    els.sheetTitle.textContent = t("loading");
    try {
      const building = await api("/api/buildings/reverse-geocode", {
        method: "POST",
        body: JSON.stringify({ lat: e.latlng.lat, lon: e.latlng.lng }),
      });
      await selectBuilding(building);
      await refreshCityAggregates(activeCityId || building.cityId);
      await loadBuildingMarkers();
    } catch (err) {
      els.sheetTitle.textContent = err?.body?.error || err?.message || t("geocodeFail");
      els.sheetMeta.textContent = t("geocodeHint");
      els.addNoteBtn.classList.add("hidden");
      els.notesList.innerHTML = "";
    }
    return;
  }
  if (lockedCityId) {
    await clearSelection();
    return;
  }
  if (mode === "city") {
    const city = nearestCity(e.latlng);
    if (city) selectCity(city);
  }
}

async function selectCity(city) {
  await enterCity(city, { persist: true });
}

async function selectDistrict(d) {
  selectedDistrictId = d.districtId || d.id || null;
  applyDistrictStyles();
  context = {
    level: "District",
    cityId: d.cityId || activeCityId,
    districtId: selectedDistrictId,
    title: d.name,
  };
  setSheetSnap("half");
  await refreshSheet();
  await enrichDistrictSheet(selectedDistrictId, document.getElementById("housing-district-slot"));
}

async function selectBuilding(b) {
  selectedDistrictId = null;
  applyDistrictStyles();
  context = {
    level: "Building",
    cityId: b.cityId,
    districtId: b.districtId,
    buildingId: b.buildingId,
    title: b.addressLine,
  };
  setSheetSnap("half");
  await refreshSheet();
  const slot = document.getElementById("housing-district-slot");
  slot.innerHTML = "";
  const add = document.createElement("button");
  add.type = "button";
  add.className = "btn primary";
  add.textContent = t("addOfferHere");
  add.onclick = () => {
    import("./housing.js").then(({ openOfferAt }) =>
      openOfferAt({
        lat: b.lat,
        lon: b.lon,
        cityId: b.cityId,
        districtId: b.districtId,
        buildingId: b.buildingId,
        title: b.addressLine,
      }));
  };
  slot.appendChild(add);
}

async function refreshSheet() {
  if (!context) return;
  if (!requireAuthOrGate()) {
    showAuthError({ message: t("sessionExpired") });
    return;
  }
  els.sheetTitle.textContent = context.title;
  const canAdd = context.level === "City" || context.level === "Point" || context.level === "Building";
  els.addNoteBtn.classList.toggle("hidden", !canAdd);
  els.notesList.innerHTML = `<li class="empty-notes">${t("loading")}</li>`;

  let aggPath = `/api/aggregates/city/${context.cityId}`;
  let notesPath = `/api/notes?cityId=${context.cityId}&level=City`;
  if (context.level === "District") {
    aggPath = `/api/aggregates/district/${context.districtId}`;
    notesPath = `/api/notes?districtId=${context.districtId}&level=Point`;
  } else if (context.level === "Point") {
    notesPath = `/api/notes?cityId=${context.cityId}&level=Point`;
    aggPath = context.districtId
      ? `/api/aggregates/district/${context.districtId}`
      : `/api/aggregates/city/${context.cityId}`;
  } else if (context.level === "Building") {
    aggPath = `/api/aggregates/building/${context.buildingId}`;
    notesPath = `/api/notes?buildingId=${context.buildingId}`;
  }

  try {
    const [agg, notes] = await Promise.all([api(aggPath), api(notesPath)]);
    let list = notes;
    if (context.level === "Point" && context.lat != null) {
      list = notes.filter(
        (n) =>
          n.noteId === editingNoteId ||
          (n.lat != null &&
            Math.abs(n.lat - context.lat) < 1e-5 &&
            Math.abs(n.lon - context.lon) < 1e-5)
      );
    }
    if (pendingMoveNote) {
      els.sheetMeta.textContent = t("movingNoteHint");
    } else {
      els.sheetMeta.textContent =
        agg.noteCount > 0 && agg.scoreOverall != null
          ? `${t("avgScore")}: ${agg.scoreOverall.toFixed(1)} (${agg.noteCount})`
          : "";
    }
    if (!pendingMoveNote && context.level === "District" && context.districtId) {
      const envLine = formatEnvMeta(context.districtId);
      els.sheetMeta.textContent = els.sheetMeta.textContent
        ? `${els.sheetMeta.textContent} · ${envLine}`
        : envLine;
    }

    if (!list.length) {
      const emptyHint =
        context.level === "City" ? t("dragToPlaceNote") : t("noNotes");
      els.notesList.innerHTML = `<li class="empty-notes">${emptyHint}</li>`;
      return;
    }

    els.notesList.innerHTML = "";
    for (const n of list) {
      const li = document.createElement("li");
      li.className = "note-card";
      const radiusMeta = n.radiusMeters != null ? ` · ${n.radiusMeters}m` : "";
      const canMove = n.level === "Point" || (n.lat != null && n.lon != null);
      const movingThis = pendingMoveNote && pendingMoveNote.noteId === n.noteId;
      li.innerHTML = `
        <div><span class="score">${n.scoreOverall}/10</span><span class="meta">${n.level}${radiusMeta}</span></div>
        <p></p>
        <div class="note-actions">
          ${canMove ? `<button type="button" class="btn ghost move">${movingThis ? t("cancel") : t("moveNote")}</button>` : ""}
          <button type="button" class="btn ghost edit">${t("editNote")}</button>
          <button type="button" class="btn ghost danger del">${t("delete")}</button>
        </div>`;
      li.querySelector("p").textContent = n.text;
      li.querySelector(".move")?.addEventListener("click", () => startPointMove(n));
      li.querySelector(".edit").onclick = () => openNoteForm(n);
      li.querySelector(".del").onclick = async () => {
        await api(`/api/notes/${n.noteId}`, { method: "DELETE" });
        await refreshSheet();
        await reloadDistrictColors();
        await loadPointNotes();
        if (context.level === "Building") {
          await refreshCityAggregates(activeCityId || context.cityId);
          await loadBuildingMarkers();
        }
      };
      els.notesList.appendChild(li);
    }
    if (isMobileSheet() && list.length > 2) setSheetSnap("full");
  } catch (err) {
    if (err.status === 401) showAuthError({ message: t("sessionExpired") });
    else throw err;
  }
}

/** Coords for the note being created/edited (Point level). */
let formPointCoords = null;

function openNoteForm(note = null) {
  editingNoteId = note?.noteId ?? null;
  if (note?.lat != null && note?.lon != null) {
    formPointCoords = { lat: note.lat, lon: note.lon };
  } else if (context?.level === "Point" && context.lat != null && context.lon != null) {
    formPointCoords = { lat: context.lat, lon: context.lon };
  } else {
    formPointCoords = null;
  }
  els.dialogTitle.textContent = note ? t("editNote") : t("addNote");
  els.noteText.value = note?.text ?? "";
  els.scoreOverall.value = note?.scoreOverall ?? 7;
  els.scoreOverallOut.textContent = els.scoreOverall.value;
  document.getElementById("score-nature").value = note?.scoreNature ?? "";
  document.getElementById("score-shops").value = note?.scoreShops ?? "";
  document.getElementById("score-transport").value = note?.scoreTransport ?? "";
  document.getElementById("score-safety").value = note?.scoreSafety ?? "";
  const radiusWrap = document.getElementById("note-radius-wrap");
  const radiusInput = document.getElementById("note-radius");
  const showRadius = formPointCoords != null || note?.level === "Point" || context?.level === "Point";
  radiusWrap.classList.toggle("hidden", !showRadius);
  if (showRadius) {
    radiusInput.value = note?.radiusMeters ?? context?.radiusMeters ?? DEFAULT_POINT_RADIUS;
  }
  stopNoteVoice();
  document.getElementById("place-note-fab")?.classList.add("hidden");
  els.dialog.showModal();
}

function optScore(id) {
  const v = document.getElementById(id).value;
  if (v === "" || v == null) return null;
  return Number(v);
}

els.scoreOverall.addEventListener("input", () => {
  els.scoreOverallOut.textContent = els.scoreOverall.value;
});

els.addNoteBtn.addEventListener("click", () => openNoteForm());
document.getElementById("note-cancel").addEventListener("click", () => {
  stopNoteVoice();
  els.dialog.close();
});

els.form.addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!context) return;
  const isPoint =
    context.level === "Point" ||
    (formPointCoords != null && (editingNoteId || context.level === "District"));
  const level = isPoint ? "Point" : context.level;
  if (level === "District") return; // no whole-district notes
  const body = {
    level,
    targetCityId: context.cityId,
    targetDistrictId: context.districtId ?? null,
    targetBuildingId: context.buildingId ?? null,
    text: els.noteText.value.trim(),
    scoreOverall: Number(els.scoreOverall.value),
    scoreNature: optScore("score-nature"),
    scoreShops: optScore("score-shops"),
    scoreTransport: optScore("score-transport"),
    scoreSafety: optScore("score-safety"),
  };
  if (level === "Point") {
    body.lat = formPointCoords?.lat ?? context.lat;
    body.lon = formPointCoords?.lon ?? context.lon;
    if (body.lat == null || body.lon == null) return;
    const r = Number(document.getElementById("note-radius").value);
    body.radiusMeters = Number.isFinite(r) ? r : DEFAULT_POINT_RADIUS;
  }

  let saved;
  if (editingNoteId) {
    saved = await api(`/api/notes/${editingNoteId}`, { method: "PUT", body: JSON.stringify(body) });
  } else {
    saved = await api("/api/notes", { method: "POST", body: JSON.stringify(body) });
  }
  stopNoteVoice();
  els.dialog.close();
  editingNoteId = null;
  formPointCoords = null;
  if (level === "Point" && saved?.targetDistrictId) {
    context.districtId = saved.targetDistrictId;
    selectedDistrictId = saved.targetDistrictId;
    applyDistrictStyles();
    await enrichDistrictSheet(saved.targetDistrictId, document.getElementById("housing-district-slot"));
  }
  await reloadDistrictColors();
  await loadPointNotes();
  await refreshSheet();
  if (level === "Point") setSheetSnap("half");
  if (context.level === "Building") {
    await refreshCityAggregates(activeCityId || context.cityId);
    await loadBuildingMarkers();
  }
});

document.getElementById("lang-toggle").addEventListener("click", () => {
  toggleLang();
  updateZoomLabel();
  updatePlaceNoteFabLabel();
  updateSheetHandleAria();
  updateMapModeToggle();
  if (noteVoice?.isListening()) updateNoteVoiceUi(true);
  else if (els.noteVoiceStatus?.classList.contains("is-error")) {
    // keep error text; refresh button labels only
    if (els.noteVoiceBtn) {
      els.noteVoiceBtn.setAttribute("title", t("voiceInputStart"));
      els.noteVoiceBtn.setAttribute("aria-label", t("voiceInputStart"));
    }
  } else resetNoteVoiceUi();
  if (context) refreshSheet();
});

document.getElementById("sign-out").addEventListener("click", () => {
  clearToken();
  location.reload();
});

