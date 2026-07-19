import { api, getToken, setToken, clearToken, isTokenExpired } from "./api.js";
import { applyI18n, t, toggleLang } from "./i18n.js";

const ZOOM_CITY = 10;
const ZOOM_DISTRICT = 14;
const ZOOM_INTO_DISTRICT = 12;
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

let cities = [];
let activeCityId = null;
let fittedForCityId = null;
let context = null;
let editingNoteId = null;
/** @type {AbortController | null} */
let mapAbort = null;
let moveTimer = null;
/** @type {Record<string, number|null>} */
let districtScores = {};
/** @type {Record<string, number|null>} */
let buildingScores = {};

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
};

function scoreColor(score) {
  if (score == null) return "#9aadb6";
  const tNorm = Math.max(0, Math.min(1, (score - 1) / 9));
  const r = Math.round(179 + (13 - 179) * tNorm);
  const g = Math.round(58 + (110 - 58) * tNorm);
  const b = Math.round(58 + (110 - 58) * tNorm);
  return `rgb(${r},${g},${b})`;
}

function currentMode(zoom) {
  if (zoom <= ZOOM_CITY) return "city";
  if (zoom <= ZOOM_DISTRICT) return "district";
  return "building";
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

async function initAuth() {
  applyI18n();
  const cfg = await fetch("/api/config").then((r) => r.json());
  const clientId = cfg.googleClientId;

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
}

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
  // Leaflet measures a hidden/zero-size container wrongly — refit after layout
  requestAnimationFrame(() => {
    requestAnimationFrame(() => fitPolandView());
  });
  cities = await api("/api/cities");
  renderCityMarkers();
  updateZoomLabel();
  fitPolandView();
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

  cityLayer.addTo(map);
  buildingLayer.addTo(map);

  map.on("zoomend", scheduleMapUpdate);
  map.on("moveend", scheduleMapUpdate);
  map.on("click", onMapClick);
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
  const mode = currentMode(map.getZoom());
  if (mode === "city") {
    clearDistricts();
    buildingLayer.clearLayers();
    cityLayer.addTo(map);
    return;
  }

  map.removeLayer(cityLayer);
  const city = nearestCity(map.getCenter());
  if (city && (city.districtCount ?? 0) === 0) {
    clearDistricts();
    buildingLayer.clearLayers();
    return;
  }

  if (city && city.cityId !== activeCityId) {
    await loadDistricts(city.cityId, { fit: true });
  } else if (city && !districtLayer) {
    await loadDistricts(city.cityId, { fit: true });
  }

  setDistrictInteractive(mode === "district");
  if (mode === "building") await loadBuildingMarkers();
  else buildingLayer.clearLayers();
}

function setDistrictInteractive(interactive) {
  if (!districtLayer) return;
  districtLayer.eachLayer((layer) => {
    const el = layer.getElement?.() || layer._path;
    if (el) el.style.pointerEvents = interactive ? "auto" : "none";
    if (layer.setStyle) {
      layer.setStyle({
        fillOpacity: interactive ? 0.45 : 0.18,
        weight: interactive ? 1.5 : 1,
      });
    }
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
  activeCityId = null;
  fittedForCityId = null;
  districtScores = {};
  buildingScores = {};
}

async function refreshCityAggregates(cityId, signal) {
  const batch = await api(`/api/cities/${cityId}/aggregates`, { signal });
  districtScores = {};
  buildingScores = {};
  for (const d of batch.districts || []) districtScores[d.id] = d.scoreOverall;
  for (const b of batch.buildings || []) buildingScores[b.id] = b.scoreOverall;
  return batch;
}

async function loadDistricts(cityId, { fit = false } = {}) {
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
    style: (f) => ({
      color: "#1a2b33",
      weight: 1.5,
      fillColor: scoreColor(f.properties.score),
      fillOpacity: 0.45,
      interactive: currentMode(map.getZoom()) === "district",
    }),
    onEachFeature: (feature, layer) => {
      const name = feature.properties.name || "";
      const area = feature.properties.areaKm2;
      const areaTxt = area != null ? `<br>${area} km²` : "";
      layer.bindTooltip(name);
      layer.bindPopup(`<strong>${name}</strong>${areaTxt}`);
      layer.on("mouseover", () => {
        if (currentMode(map.getZoom()) !== "district") return;
        layer.setStyle({ weight: 3, fillOpacity: 0.65 });
      });
      layer.on("mouseout", () => {
        districtLayer.resetStyle(layer);
        setDistrictInteractive(currentMode(map.getZoom()) === "district");
      });
      layer.on("click", (e) => {
        if (currentMode(map.getZoom()) !== "district") return;
        L.DomEvent.stopPropagation(e);
        selectDistrict(feature.properties);
      });
    },
  }).addTo(map);

  setDistrictInteractive(currentMode(map.getZoom()) === "district");

  if (fit && fittedForCityId !== cityId) {
    fittedForCityId = cityId;
    try {
      map.fitBounds(districtLayer.getBounds(), { padding: [20, 20], maxZoom: 13 });
    } catch { /* empty */ }
  }
}

async function reloadDistrictColors() {
  if (!activeCityId || !districtLayer) return;
  await refreshCityAggregates(activeCityId);
  districtLayer.eachLayer((layer) => {
    const id = layer.feature?.properties?.id;
    if (id) layer.feature.properties.score = districtScores[id] ?? null;
    districtLayer.resetStyle(layer);
  });
  setDistrictInteractive(currentMode(map.getZoom()) === "district");
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
  const mode = currentMode(map.getZoom());
  if (mode === "building") {
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
  } else if (mode === "city") {
    const city = nearestCity(e.latlng);
    if (city) selectCity(city);
  }
}

async function selectCity(city) {
  context = { level: "City", cityId: city.cityId, title: city.name };
  // Enter district zoom band so polygons load
  map.setView([city.centerLat, city.centerLon], ZOOM_INTO_DISTRICT);
  await refreshSheet();
}

async function selectDistrict(d) {
  context = {
    level: "District",
    cityId: d.cityId || activeCityId,
    districtId: d.districtId || d.id,
    title: d.name,
  };
  await refreshSheet();
}

async function selectBuilding(b) {
  context = {
    level: "Building",
    cityId: b.cityId,
    districtId: b.districtId,
    buildingId: b.buildingId,
    title: b.addressLine,
  };
  await refreshSheet();
}

async function refreshSheet() {
  if (!context) return;
  if (!requireAuthOrGate()) {
    showAuthError({ message: t("sessionExpired") });
    return;
  }
  els.sheetTitle.textContent = context.title;
  els.addNoteBtn.classList.remove("hidden");
  els.notesList.innerHTML = `<li class="empty-notes">${t("loading")}</li>`;

  let aggPath = `/api/aggregates/city/${context.cityId}`;
  let notesPath = `/api/notes?cityId=${context.cityId}&level=City`;
  if (context.level === "District") {
    aggPath = `/api/aggregates/district/${context.districtId}`;
    notesPath = `/api/notes?districtId=${context.districtId}`;
  } else if (context.level === "Building") {
    aggPath = `/api/aggregates/building/${context.buildingId}`;
    notesPath = `/api/notes?buildingId=${context.buildingId}`;
  }

  try {
    const [agg, notes] = await Promise.all([api(aggPath), api(notesPath)]);
    els.sheetMeta.textContent =
      agg.noteCount > 0 && agg.scoreOverall != null
        ? `${t("avgScore")}: ${agg.scoreOverall.toFixed(1)} (${agg.noteCount})`
        : "";

    if (!notes.length) {
      els.notesList.innerHTML = `<li class="empty-notes">${t("noNotes")}</li>`;
      return;
    }

    els.notesList.innerHTML = "";
    for (const n of notes) {
      const li = document.createElement("li");
      li.className = "note-card";
      li.innerHTML = `
        <div><span class="score">${n.scoreOverall}/10</span><span class="meta">${n.level}</span></div>
        <p></p>
        <div class="note-actions">
          <button type="button" class="btn ghost edit">${t("editNote")}</button>
          <button type="button" class="btn ghost danger del">${t("delete")}</button>
        </div>`;
      li.querySelector("p").textContent = n.text;
      li.querySelector(".edit").onclick = () => openNoteForm(n);
      li.querySelector(".del").onclick = async () => {
        await api(`/api/notes/${n.noteId}`, { method: "DELETE" });
        await refreshSheet();
        if (context.level === "District") await reloadDistrictColors();
        if (context.level === "Building") {
          await refreshCityAggregates(activeCityId || context.cityId);
          await loadBuildingMarkers();
        }
      };
      els.notesList.appendChild(li);
    }
  } catch (err) {
    if (err.status === 401) showAuthError({ message: t("sessionExpired") });
    else throw err;
  }
}

function openNoteForm(note = null) {
  editingNoteId = note?.noteId ?? null;
  els.dialogTitle.textContent = note ? t("editNote") : t("addNote");
  els.noteText.value = note?.text ?? "";
  els.scoreOverall.value = note?.scoreOverall ?? 7;
  els.scoreOverallOut.textContent = els.scoreOverall.value;
  document.getElementById("score-nature").value = note?.scoreNature ?? "";
  document.getElementById("score-shops").value = note?.scoreShops ?? "";
  document.getElementById("score-transport").value = note?.scoreTransport ?? "";
  document.getElementById("score-safety").value = note?.scoreSafety ?? "";
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
document.getElementById("note-cancel").addEventListener("click", () => els.dialog.close());

els.form.addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!context) return;
  const body = {
    level: context.level,
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

  if (editingNoteId) {
    await api(`/api/notes/${editingNoteId}`, { method: "PUT", body: JSON.stringify(body) });
  } else {
    await api("/api/notes", { method: "POST", body: JSON.stringify(body) });
  }
  els.dialog.close();
  await refreshSheet();
  if (context.level === "District") await reloadDistrictColors();
  if (context.level === "Building") {
    await refreshCityAggregates(activeCityId || context.cityId);
    await loadBuildingMarkers();
  }
});

document.getElementById("lang-toggle").addEventListener("click", () => {
  toggleLang();
  updateZoomLabel();
  if (context) refreshSheet();
});

document.getElementById("sign-out").addEventListener("click", () => {
  clearToken();
  location.reload();
});

document.getElementById("sheet-handle").addEventListener("click", () => {
  els.sheet.classList.toggle("expanded");
});

function waitGoogle() {
  if (window.google?.accounts?.id) initAuth();
  else setTimeout(waitGoogle, 50);
}
waitGoogle();
