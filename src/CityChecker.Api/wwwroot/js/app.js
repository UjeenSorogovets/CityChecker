import { api, getToken, setToken, clearToken } from "./api.js";
import { applyI18n, t, toggleLang } from "./i18n.js";

const ZOOM_CITY = 10;
const ZOOM_DISTRICT = 14;
const POLAND_CENTER = [52.1, 19.4];
const POLAND_BOUNDS = L.latLngBounds([49.0, 14.0], [55.0, 25.0]);

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
let context = null; // { level, cityId, districtId?, buildingId?, title }
let editingNoteId = null;

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

async function initAuth() {
  applyI18n();
  const cfg = await fetch("/api/config").then((r) => r.json());
  const clientId = cfg.googleClientId;

  if (getToken()) {
    try {
      await api("/api/cities");
      showApp();
      return;
    } catch (err) {
      showAuthError(err);
      clearToken();
    }
  }

  window.google.accounts.id.initialize({
    client_id: clientId,
    callback: async (response) => {
      setToken(response.credential);
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
  applyI18n();
  initMap();
  cities = await api("/api/cities");
  renderCityMarkers();
  updateZoomLabel();
}

function initMap() {
  if (map) return;
  map = L.map("map", {
    center: POLAND_CENTER,
    zoom: 6,
    maxBounds: POLAND_BOUNDS.pad(0.2),
    minZoom: 5,
    zoomControl: true,
  });
  L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
  }).addTo(map);

  cityLayer.addTo(map);
  buildingLayer.addTo(map);

  map.on("zoomend", onZoomOrMove);
  map.on("moveend", onZoomOrMove);
  map.on("click", onMapClick);
}

function renderCityMarkers() {
  cityLayer.clearLayers();
  for (const c of cities) {
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
  updateZoomLabel();
  const mode = currentMode(map.getZoom());
  if (mode === "city") {
    clearDistricts();
    buildingLayer.clearLayers();
    cityLayer.addTo(map);
  } else {
    map.removeLayer(cityLayer);
    const city = nearestCity(map.getCenter());
    if (city && city.cityId !== activeCityId) {
      activeCityId = city.cityId;
      await loadDistricts(city.cityId);
    } else if (city && !districtLayer) {
      await loadDistricts(city.cityId);
    }
    if (mode === "building") {
      await loadBuildingMarkers();
    } else {
      buildingLayer.clearLayers();
    }
  }
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
}

async function loadDistricts(cityId) {
  clearDistricts();
  activeCityId = cityId;
  const districts = await api(`/api/cities/${cityId}/districts`);
  const features = [];
  for (const d of districts) {
    let score = null;
    try {
      const agg = await api(`/api/aggregates/district/${d.districtId}`);
      score = agg.scoreOverall;
    } catch { /* ignore */ }
    features.push({
      type: "Feature",
      properties: { ...d, score },
      geometry: d.geometry,
    });
  }

  districtLayer = L.geoJSON(
    { type: "FeatureCollection", features },
    {
      style: (f) => ({
        color: "#1a2b33",
        weight: 1,
        fillColor: scoreColor(f.properties.score),
        fillOpacity: 0.45,
      }),
      onEachFeature: (feature, layer) => {
        layer.bindTooltip(feature.properties.name);
        layer.on("click", (e) => {
          L.DomEvent.stopPropagation(e);
          if (currentMode(map.getZoom()) !== "district") return;
          selectDistrict(feature.properties);
        });
      },
    }
  ).addTo(map);
}

async function loadBuildingMarkers() {
  if (!activeCityId) return;
  const b = map.getBounds();
  const qs = new URLSearchParams({
    minLat: b.getSouth(),
    minLon: b.getWest(),
    maxLat: b.getNorth(),
    maxLon: b.getEast(),
  });
  const buildings = await api(`/api/cities/${activeCityId}/buildings?${qs}`);
  buildingLayer.clearLayers();
  for (const bld of buildings) {
    let score = null;
    try {
      const agg = await api(`/api/aggregates/building/${bld.buildingId}`);
      score = agg.scoreOverall;
    } catch { /* ignore */ }
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
  const mode = currentMode(map.getZoom());
  if (mode === "building") {
    els.sheetTitle.textContent = t("loading");
    try {
      const building = await api("/api/buildings/reverse-geocode", {
        method: "POST",
        body: JSON.stringify({ lat: e.latlng.lat, lon: e.latlng.lng }),
      });
      await selectBuilding(building);
      await loadBuildingMarkers();
    } catch {
      els.sheetTitle.textContent = t("geocodeFail");
      els.addNoteBtn.classList.add("hidden");
      els.notesList.innerHTML = "";
      els.sheetMeta.textContent = "";
    }
  } else if (mode === "city") {
    const city = nearestCity(e.latlng);
    if (city) selectCity(city);
  }
}

async function selectCity(city) {
  context = { level: "City", cityId: city.cityId, title: city.name };
  map.setView([city.centerLat, city.centerLon], Math.max(map.getZoom(), ZOOM_CITY));
  await refreshSheet();
}

async function selectDistrict(d) {
  context = {
    level: "District",
    cityId: d.cityId,
    districtId: d.districtId,
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
      if (context.level === "District" && activeCityId) await loadDistricts(activeCityId);
      if (context.level === "Building") await loadBuildingMarkers();
    };
    els.notesList.appendChild(li);
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
  if (context.level === "District" && activeCityId) await loadDistricts(activeCityId);
  if (context.level === "Building") await loadBuildingMarkers();
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

// Wait for GIS script
function waitGoogle() {
  if (window.google?.accounts?.id) initAuth();
  else setTimeout(waitGoogle, 50);
}
waitGoogle();
