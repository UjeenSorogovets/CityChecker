import { api, getToken, setToken, clearToken, isTokenExpired } from "./api.js";
import { applyI18n, t, toggleLang } from "./i18n.js";
import { initHousing, housingMapClick, enrichDistrictSheet } from "./housing.js";

const ZOOM_CITY = 10;
const ZOOM_DISTRICT = 14;
const ZOOM_INTO_DISTRICT = 12;
const LOCKED_MIN_ZOOM = 11;
const CITY_STORAGE_KEY = "cc_city_id";
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

let cities = [];
let activeCityId = null;
/** Locked working city — one at a time */
let lockedCityId = null;
/** @type {string|null} */
let selectedDistrictId = null;
let context = null;
let editingNoteId = null;
const DEFAULT_POINT_RADIUS = 300;
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
  buildingLayer.clearLayers();
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

  // Picker overlay was covering the map — size/center can be wrong until layout settles.
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  map.invalidateSize();
  map.setView([lat, lon], ZOOM_INTO_DISTRICT, { animate: false });

  await refreshSheet();
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
  pointLayer.addTo(map);

  map.on("zoomend", scheduleMapUpdate);
  map.on("moveend", scheduleMapUpdate);
  map.on("click", onMapClick);

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
  if (mode === "building") {
    await loadBuildingMarkers();
    await loadPointNotes();
    pointLayer.bringToFront();
  } else {
    buildingLayer.clearLayers();
    await loadPointNotes();
  }
}

function setDistrictInteractive(interactive) {
  applyDistrictStyles(interactive);
  if (!districtLayer) return;
  // Normal clicks pass through so map can drop point notes; Shift+click selects district (housing).
  districtLayer.eachLayer((layer) => {
    const el = layer.getElement?.() || layer._path;
    if (el) el.style.pointerEvents = interactive ? "auto" : "none";
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
  const score = feature?.properties?.score;

  if (filteredOut) {
    return {
      color: "#b0bec5",
      weight: 0.8,
      fillColor: scoreColor(score),
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
      fillColor: scoreColor(score),
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
    fillColor: scoreColor(score),
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
  pointLayer.clearLayers();
  activeCityId = null;
  selectedDistrictId = null;
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
        // Shift+click: housing / district sheet (avg + points). Plain click: drop point note.
        if (e.originalEvent?.shiftKey) {
          selectDistrict(feature.properties);
        } else {
          selectMapPoint(e.latlng);
        }
      });
    },
  }).addTo(map);

  applyDistrictStyles();
  pointLayer.bringToFront();
  // Do not fitBounds here: on a short mobile viewport it can zoom out past ZOOM_CITY,
  // clear districts, reload, and loop. City tap already setView()'d into the band.
}

async function loadPointNotes() {
  if (!lockedCityId && !activeCityId) return;
  const cityId = lockedCityId || activeCityId;
  try {
    const notes = await api(`/api/notes?cityId=${cityId}&level=Point`);
    pointLayer.clearLayers();
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
        interactive: false, // coverage only — clicks pass through so new points can be placed inside
      });
      pointLayer.addLayer(circle);
      const dot = L.circleMarker([n.lat, n.lon], {
        radius: 5,
        color: "#1a2b33",
        fillColor: color,
        fillOpacity: 1,
        weight: 1,
      });
      dot.on("click", (e) => {
        L.DomEvent.stopPropagation(e);
        openPointNote(n);
      });
      pointLayer.addLayer(dot);
    }
  } catch (err) {
    if (err.status === 401) showAuthError({ message: t("sessionExpired") });
  }
}

async function openPointNote(n) {
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
  }
  await refreshSheet();
  openNoteForm(n);
}

async function selectMapPoint(latlng) {
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
  els.sheetTitle.textContent = context.title;
  els.addNoteBtn.classList.remove("hidden");
  els.sheetMeta.textContent = "";
  els.notesList.innerHTML = `<li class="empty-notes">${t("dropPoint")}</li>`;
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
  if (housingMapClick(e.latlng)) return;
  const mode = currentMode(map.getZoom());
  // Locked city: empty-map click always drops a point (incl. building zoom).
  // Building notes: click a building marker. Shift+click: reverse-geocode building.
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
    await selectMapPoint(e.latlng);
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
    els.sheetMeta.textContent =
      agg.noteCount > 0 && agg.scoreOverall != null
        ? `${t("avgScore")}: ${agg.scoreOverall.toFixed(1)} (${agg.noteCount})`
        : "";

    if (!list.length) {
      els.notesList.innerHTML = `<li class="empty-notes">${t("noNotes")}</li>`;
      return;
    }

    els.notesList.innerHTML = "";
    for (const n of list) {
      const li = document.createElement("li");
      li.className = "note-card";
      const radiusMeta = n.radiusMeters != null ? ` · ${n.radiusMeters}m` : "";
      li.innerHTML = `
        <div><span class="score">${n.scoreOverall}/10</span><span class="meta">${n.level}${radiusMeta}</span></div>
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
        await reloadDistrictColors();
        await loadPointNotes();
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

