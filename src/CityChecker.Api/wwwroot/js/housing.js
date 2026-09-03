import { api } from "./api.js";
import { t } from "./i18n.js?v=flats1";

/** @type {L.LayerGroup | null} */
let offerLayer = null;
/** @type {L.LayerGroup | null} */
let flatPinsLayer = null;
/** @type {{ map: L.Map, getActiveCityId: () => string|null, getContext: () => any }} */
let ctx = null;

const FLAT_FILTERS_KEY = "cc_flat_filters";
const FLAT_DEFAULTS = { priceMax: 650000, areaMin: 50, rooms: ["TWO", "THREE", "FOUR", "FIVE", "SIX_OR_MORE"] };
let flatPinsEnabled = false;
let flatPinsTimer = null;
let flatPinsGen = 0;
let offersAllowed = false;

export function initHousing(options) {
  ctx = options;
  offerLayer = L.layerGroup().addTo(ctx.map);
  flatPinsLayer = L.layerGroup().addTo(ctx.map);
  void setupOffersAccess();
}

async function setupOffersAccess() {
  try {
    const access = await api("/api/housing/offers-access");
    offersAllowed = !!access?.allowed;
  } catch {
    offersAllowed = false;
  }
  if (!offersAllowed) {
    document.getElementById("offers-toggle")?.classList.add("hidden");
    document.getElementById("offers-panel")?.classList.add("hidden");
    return;
  }
  wireUi();
  refreshOffers();
}

export function openOfferAt({ lat, lon, cityId, districtId, buildingId, title }) {
  openOfferDialog({ lat, lon, districtId, title, cityId, buildingId });
}

function wireUi() {
  const offersPanel = document.getElementById("offers-panel");
  const offersToggle = document.getElementById("offers-toggle");

  offersToggle?.addEventListener("click", () => {
    const open = !offersPanel?.classList.contains("open");
    offersPanel?.classList.toggle("open", open);
    if (open) {
      refreshOffersList();
      if (flatPinsEnabled) scheduleFlatPinsReload(true);
    }
  });

  loadFlatFiltersIntoUi();
  for (const id of ["flat-price-max", "flat-area-min"]) {
    document.getElementById(id)?.addEventListener("change", () => {
      persistFlatFilters();
      if (flatPinsEnabled) scheduleFlatPinsReload();
    });
  }
  document.querySelectorAll('input[name="flat-room"]').forEach((el) => {
    el.addEventListener("change", () => {
      persistFlatFilters();
      if (flatPinsEnabled) scheduleFlatPinsReload();
    });
  });
  document.getElementById("flat-show")?.addEventListener("change", (e) => {
    flatPinsEnabled = !!e.target.checked;
    if (!flatPinsEnabled) {
      flatPinsLayer?.clearLayers();
      setFlatStatus(t("flatHint"));
      return;
    }
    scheduleFlatPinsReload(true);
  });
  ctx.map.on("moveend", () => {
    if (flatPinsEnabled) scheduleFlatPinsReload();
  });
}

function openOfferDialog(seed = {}) {
  const title = prompt(t("offerTitle"), seed.title || "");
  if (!title) return;
  const url = prompt(t("offerUrl"), seed.url || "") || null;
  const modeDefault = seed.mode === "Buy" ? "Buy" : seed.mode === "Rent" ? "Rent" : "Rent";
  const mode = (prompt(t("offerMode"), modeDefault) || modeDefault).toLowerCase().startsWith("b") ? "Buy" : "Rent";
  const price = numPrompt(t("offerPrice"), seed.price ?? null);
  const sqm = numPrompt(t("offerSqm"), seed.sqm ?? null);
  const rent = numPrompt(t("offerMonthly"), null);
  const media = numPrompt(t("offerMedia"), null);
  const czynsz = numPrompt(t("offerCzynsz"), null);
  const deal = numPrompt(t("offerDealOverall"), 7);
  const scoreLayout = numPrompt(t("scoreLayout"), deal);
  const scoreLight = numPrompt(t("scoreLight"), deal);
  const scoreCondition = numPrompt(t("scoreCondition"), deal);
  const flaw = prompt(t("offerKillerFlaw"), "") || null;
  const photos = prompt(t("offerPhotos"), "") || null;
  const voice = prompt(t("offerVoice"), "") || null;
  const finalist = confirm(t("offerFinalist"));
  const reminderWhen = prompt(t("reminderWhenOptional"), "") || null;

  const c = ctx?.getContext?.() || {};
  const center = ctx.map.getCenter();
  const body = {
    cityId: seed.cityId || c.cityId || null,
    districtId: seed.districtId || c.districtId || null,
    buildingId: seed.buildingId || c.buildingId || null,
    title,
    url,
    mode,
    lat: seed.lat ?? center.lat,
    lon: seed.lon ?? center.lng,
    price,
    sqm,
    rentOrMortgage: rent,
    media,
    czynsz,
    scorePrice: deal,
    scoreLayout: scoreLayout ?? deal,
    scoreLight: scoreLight ?? deal,
    scoreCondition: scoreCondition ?? deal,
    killerFlaw: flaw,
    photoUrls: photos,
    voiceNoteUrl: voice,
    isFinalist: finalist,
    reminderAt: reminderWhen ? new Date(reminderWhen).toISOString() : null,
    hasKsiega: mode === "Buy" ? confirm(t("offerKsiega")) : null,
    hasSluzebnosc: mode === "Buy" ? confirm(t("offerSluzebnosc")) : null,
    hasSpoldzielniaDebt: mode === "Buy" ? confirm(t("offerSpoldzielnia")) : null,
    deposit: mode === "Rent" ? numPrompt(t("offerDeposit"), null) : null,
    noticeDays: mode === "Rent" ? numPrompt(t("offerNoticeDays"), null) : null,
    furnished: mode === "Rent" ? confirm(t("offerFurnished")) : null,
    pricePerSqm: mode === "Buy" && price && sqm ? Math.round((price / sqm) * 100) / 100 : null,
    renovationBudget: mode === "Buy" ? numPrompt(t("offerRenovation"), null) : null,
  };
  api("/api/housing/offers", { method: "POST", body: JSON.stringify(body) })
    .then(() => { refreshOffers(); refreshOffersList(); })
    .catch(alertErr);
}

async function refreshOffers() {
  if (!offerLayer) return;
  offerLayer.clearLayers();
  const list = await api("/api/housing/offers");
  for (const o of list) {
    const m = L.circleMarker([o.lat, o.lon], {
      radius: o.isFinalist ? 9 : 6,
      color: o.mode === "Buy" ? "#0d6e6e" : "#3a5fb3",
      fillColor: o.mode === "Buy" ? "#0d6e6e" : "#3a5fb3",
      fillOpacity: 0.85,
      weight: 2,
    });
    m.bindTooltip(`${o.title} (${o.monthlyTotal != null ? o.monthlyTotal + " zł/mo" : o.price ?? "—"})`);
    m.on("click", (e) => {
      L.DomEvent.stopPropagation(e);
      const rem = o.reminderAt ? `\n${t("setReminder")}: ${o.reminderAt}` : "";
      alert(`${o.title}\n${o.url || ""}\n${t("dealAvg")}: ${o.dealAvg ?? "—"}\n${t("monthlyTotal")}: ${o.monthlyTotal ?? "—"}${rem}`);
    });
    offerLayer.addLayer(m);
  }
}

async function refreshOffersList() {
  const el = document.getElementById("housing-offers-list");
  if (!el) return;
  const list = await api("/api/housing/offers");
  el.innerHTML = "";
  for (const o of list) {
    const li = document.createElement("li");
    li.innerHTML = `<strong>${o.title}</strong> · ${o.mode} · ${o.monthlyTotal ?? "—"} zł
      <button type="button" class="btn ghost" data-fin>${o.isFinalist ? "★" : "☆"}</button>
      <button type="button" class="btn ghost danger" data-del>×</button>`;
    li.querySelector("[data-fin]").onclick = async () => {
      await api(`/api/housing/offers/${o.offerId}`, {
        method: "PUT",
        body: JSON.stringify({ ...o, isFinalist: !o.isFinalist }),
      });
      refreshOffers();
      refreshOffersList();
    };
    li.querySelector("[data-del]").onclick = async () => {
      await api(`/api/housing/offers/${o.offerId}`, { method: "DELETE" });
      refreshOffers();
      refreshOffersList();
    };
    el.appendChild(li);
  }
}

function setFlatStatus(text) {
  const el = document.getElementById("flat-status");
  if (el) el.textContent = text;
}

function readStoredFlatFilters() {
  try {
    const raw = localStorage.getItem(FLAT_FILTERS_KEY)
      || localStorage.getItem("cc_otodom_filters"); // migrate once from older storage key
    return JSON.parse(raw || "null");
  } catch {
    return null;
  }
}

function readFlatFilters() {
  const stored = readStoredFlatFilters();
  const priceMax = Number(document.getElementById("flat-price-max")?.value);
  const areaMin = Number(document.getElementById("flat-area-min")?.value);
  const rooms = [...document.querySelectorAll('input[name="flat-room"]:checked')].map((el) => el.value);
  return {
    priceMax: Number.isFinite(priceMax) && priceMax > 0 ? priceMax : (stored?.priceMax ?? FLAT_DEFAULTS.priceMax),
    areaMin: Number.isFinite(areaMin) && areaMin >= 0 ? areaMin : (stored?.areaMin ?? FLAT_DEFAULTS.areaMin),
    rooms: rooms.length ? rooms : (stored?.rooms?.length ? stored.rooms : FLAT_DEFAULTS.rooms),
  };
}

function loadFlatFiltersIntoUi() {
  let f = FLAT_DEFAULTS;
  const stored = readStoredFlatFilters();
  if (stored) f = { ...FLAT_DEFAULTS, ...stored };
  const priceEl = document.getElementById("flat-price-max");
  const areaEl = document.getElementById("flat-area-min");
  if (priceEl) priceEl.value = String(f.priceMax ?? FLAT_DEFAULTS.priceMax);
  if (areaEl) areaEl.value = String(f.areaMin ?? FLAT_DEFAULTS.areaMin);
  const roomSet = new Set(f.rooms?.length ? f.rooms : FLAT_DEFAULTS.rooms);
  document.querySelectorAll('input[name="flat-room"]').forEach((el) => {
    el.checked = roomSet.has(el.value);
  });
}

function persistFlatFilters() {
  localStorage.setItem(FLAT_FILTERS_KEY, JSON.stringify(readFlatFilters()));
}

function scheduleFlatPinsReload(immediate = false) {
  clearTimeout(flatPinsTimer);
  flatPinsTimer = setTimeout(() => reloadFlatPins(), immediate ? 0 : 450);
}

function flatPinsRequestBody(cityId, filters, bounds) {
  return {
    cityId,
    priceMax: filters.priceMax,
    areaMin: filters.areaMin,
    rooms: filters.rooms,
    transaction: "SELL",
    west: bounds.getWest(),
    south: bounds.getSouth(),
    east: bounds.getEast(),
    north: bounds.getNorth(),
  };
}

function formatFlatStatus(res) {
  const status = res.status || "";
  if (status === "Missing") return t("flatMissing");
  if (status === "Failed" || res.ok === false) {
    return res.error || t("flatFailed");
  }

  const n = (res.pins || []).length;
  const total = res.totalMatched;
  const listed = res.listed;
  const parts = [];
  if (n) parts.push(`${t("flatLoaded")}: ${n}`);
  else parts.push(t("flatEmpty"));
  if (total != null) {
    let mid = `${t("flatMatched")}: ${total}`;
    if (listed != null && listed < total) mid += ` (${t("flatListed")}: ${listed})`;
    parts.push(mid);
  }
  if (res.fetchedAt) {
    const d = new Date(res.fetchedAt);
    if (!Number.isNaN(d.getTime())) {
      parts.push(`${t("flatUpdated")}: ${d.toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" })}`);
    }
  }
  if (status === "Refreshing") parts.push(t("flatRefreshing"));
  return parts.join(" · ");
}

async function reloadFlatPins() {
  if (!offersAllowed || !flatPinsEnabled || !ctx?.map || !flatPinsLayer) return;
  const cityId = ctx.getActiveCityId?.();
  if (!cityId) {
    setFlatStatus(t("flatNeedCity"));
    flatPinsLayer.clearLayers();
    return;
  }
  const filters = readFlatFilters();
  persistFlatFilters();
  if (!filters.rooms.length) {
    setFlatStatus(t("flatNeedRooms"));
    flatPinsLayer.clearLayers();
    return;
  }

  const b = ctx.map.getBounds();
  const gen = ++flatPinsGen;
  setFlatStatus(t("flatLoading"));
  try {
    const res = await api("/api/housing/flat-pins", {
      method: "POST",
      body: JSON.stringify(flatPinsRequestBody(cityId, filters, b)),
    });
    if (gen !== flatPinsGen) return;
    renderFlatPins(res.pins || []);
    setFlatStatus(formatFlatStatus(res));
  } catch (e) {
    if (gen !== flatPinsGen) return;
    flatPinsLayer.clearLayers();
    setFlatStatus(e.message || t("flatEmpty"));
  }
}

function renderFlatPins(pins) {
  flatPinsLayer.clearLayers();
  for (const p of pins) {
    const price = p.price != null ? `${Math.round(p.price).toLocaleString("pl-PL")} zł` : "—";
    const area = p.areaM2 != null ? `${p.areaM2} m²` : "";
    const rooms = p.rooms ? `${p.rooms} ${t("flatRoomsShort")}` : "";
    const m = L.circleMarker([p.lat, p.lon], {
      radius: 7,
      color: "#c45c26",
      fillColor: "#e67e22",
      fillOpacity: 0.85,
      weight: 2,
    });
    const wrap = document.createElement("div");
    wrap.className = "flat-popup";
    wrap.innerHTML = `<strong>${escapeHtml([price, area, rooms].filter(Boolean).join(" · ") || t("flatOffer"))}</strong>`;
    m.bindPopup(wrap);
    flatPinsLayer.addLayer(m);
  }
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[c]));
}

function escapeAttr(s) {
  return escapeHtml(s).replace(/`/g, "&#96;");
}

function numPrompt(label, fallback) {
  const v = prompt(label, fallback == null ? "" : String(fallback));
  if (v === null || v === "") return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

function alertErr(e) {
  alert(e?.body?.error || e?.message || String(e));
}
