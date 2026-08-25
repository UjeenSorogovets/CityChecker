import { api } from "./api.js";
import { t } from "./i18n.js";

/** @type {L.LayerGroup | null} */
let offerLayer = null;
/** @type {L.LayerGroup | null} */
let otodomLayer = null;
/** @type {{ map: L.Map, getActiveCityId: () => string|null, getContext: () => any }} */
let ctx = null;

const OTODOM_FILTERS_KEY = "cc_otodom_filters";
const OTODOM_DEFAULTS = { priceMax: 650000, areaMin: 50, rooms: ["TWO", "THREE", "FOUR", "FIVE", "SIX_OR_MORE"] };
let otodomEnabled = false;
let otodomTimer = null;
let otodomGen = 0;

export function initHousing(options) {
  ctx = options;
  offerLayer = L.layerGroup().addTo(ctx.map);
  otodomLayer = L.layerGroup().addTo(ctx.map);
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
      if (otodomEnabled) scheduleOtodomReload(true);
    }
  });

  loadOtodomFiltersIntoUi();
  for (const id of ["otodom-price-max", "otodom-area-min"]) {
    document.getElementById(id)?.addEventListener("change", () => {
      persistOtodomFilters();
      if (otodomEnabled) scheduleOtodomReload();
    });
  }
  document.querySelectorAll('input[name="otodom-room"]').forEach((el) => {
    el.addEventListener("change", () => {
      persistOtodomFilters();
      if (otodomEnabled) scheduleOtodomReload();
    });
  });
  document.getElementById("otodom-show")?.addEventListener("change", (e) => {
    otodomEnabled = !!e.target.checked;
    if (!otodomEnabled) {
      otodomLayer?.clearLayers();
      setOtodomStatus(t("otodomHint"));
      return;
    }
    scheduleOtodomReload(true);
  });
  document.getElementById("otodom-refresh")?.addEventListener("click", () => {
    const show = document.getElementById("otodom-show");
    if (show && !show.checked) {
      show.checked = true;
      otodomEnabled = true;
    }
    scheduleOtodomReload(true, { refresh: true });
  });
  ctx.map.on("moveend", () => {
    if (otodomEnabled) scheduleOtodomReload();
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

function setOtodomStatus(text) {
  const el = document.getElementById("otodom-status");
  if (el) el.textContent = text;
}

function readOtodomFilters() {
  let stored = null;
  try {
    stored = JSON.parse(localStorage.getItem(OTODOM_FILTERS_KEY) || "null");
  } catch {
    stored = null;
  }
  const priceMax = Number(document.getElementById("otodom-price-max")?.value);
  const areaMin = Number(document.getElementById("otodom-area-min")?.value);
  const rooms = [...document.querySelectorAll('input[name="otodom-room"]:checked')].map((el) => el.value);
  return {
    priceMax: Number.isFinite(priceMax) && priceMax > 0 ? priceMax : (stored?.priceMax ?? OTODOM_DEFAULTS.priceMax),
    areaMin: Number.isFinite(areaMin) && areaMin >= 0 ? areaMin : (stored?.areaMin ?? OTODOM_DEFAULTS.areaMin),
    rooms: rooms.length ? rooms : (stored?.rooms?.length ? stored.rooms : OTODOM_DEFAULTS.rooms),
  };
}

function loadOtodomFiltersIntoUi() {
  let f = OTODOM_DEFAULTS;
  try {
    const stored = JSON.parse(localStorage.getItem(OTODOM_FILTERS_KEY) || "null");
    if (stored) f = { ...OTODOM_DEFAULTS, ...stored };
  } catch { /* keep defaults */ }
  const priceEl = document.getElementById("otodom-price-max");
  const areaEl = document.getElementById("otodom-area-min");
  if (priceEl) priceEl.value = String(f.priceMax ?? OTODOM_DEFAULTS.priceMax);
  if (areaEl) areaEl.value = String(f.areaMin ?? OTODOM_DEFAULTS.areaMin);
  const roomSet = new Set(f.rooms?.length ? f.rooms : OTODOM_DEFAULTS.rooms);
  document.querySelectorAll('input[name="otodom-room"]').forEach((el) => {
    el.checked = roomSet.has(el.value);
  });
}

function persistOtodomFilters() {
  localStorage.setItem(OTODOM_FILTERS_KEY, JSON.stringify(readOtodomFilters()));
}

function scheduleOtodomReload(immediate = false, opts = {}) {
  clearTimeout(otodomTimer);
  otodomTimer = setTimeout(() => reloadOtodomPins(opts), immediate ? 0 : 450);
}

function otodomRequestBody(cityId, filters, bounds) {
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

function formatOtodomStatus(res) {
  const status = res.status || "";
  if (status === "Missing") return t("otodomMissing");

  const n = (res.pins || []).length;
  const total = res.totalMatched;
  const listed = res.listed;
  const parts = [];
  if (n) parts.push(`${t("otodomLoaded")}: ${n}`);
  else parts.push(t("otodomEmpty"));
  if (total != null) {
    let mid = `${t("otodomMatched")}: ${total}`;
    if (listed != null && listed < total) mid += ` (${t("otodomListed")}: ${listed})`;
    parts.push(mid);
  }
  if (res.fetchedAt) {
    const d = new Date(res.fetchedAt);
    if (!Number.isNaN(d.getTime())) {
      parts.push(`${t("otodomUpdated")}: ${d.toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" })}`);
    }
  }
  if (status === "Failed" && res.error) parts.push(res.error);
  if (status === "Refreshing") parts.push(t("otodomRefreshing"));
  return parts.join(" · ");
}

async function reloadOtodomPins(opts = {}) {
  if (!otodomEnabled || !ctx?.map || !otodomLayer) return;
  const cityId = ctx.getActiveCityId?.();
  if (!cityId) {
    setOtodomStatus(t("otodomNeedCity"));
    otodomLayer.clearLayers();
    return;
  }
  const filters = readOtodomFilters();
  persistOtodomFilters();
  if (!filters.rooms.length) {
    setOtodomStatus(t("otodomNeedRooms"));
    otodomLayer.clearLayers();
    return;
  }

  const refresh = !!opts.refresh;
  const b = ctx.map.getBounds();
  const gen = ++otodomGen;
  setOtodomStatus(refresh ? t("otodomRefreshing") : t("otodomLoading"));
  const btn = document.getElementById("otodom-refresh");
  if (refresh && btn) btn.disabled = true;
  try {
    const path = refresh ? "/api/housing/otodom/pins/refresh" : "/api/housing/otodom/pins";
    const res = await api(path, {
      method: "POST",
      body: JSON.stringify(otodomRequestBody(cityId, filters, b)),
    });
    if (gen !== otodomGen) return;
    if (!res.ok && res.status !== "Failed") {
      otodomLayer.clearLayers();
      setOtodomStatus(res.error || t("otodomEmpty"));
      return;
    }
    renderOtodomPins(res.pins || []);
    setOtodomStatus(formatOtodomStatus(res));
  } catch (e) {
    if (gen !== otodomGen) return;
    otodomLayer.clearLayers();
    setOtodomStatus(e.message || t("otodomEmpty"));
  } finally {
    if (refresh && btn) btn.disabled = false;
  }
}

function renderOtodomPins(pins) {
  otodomLayer.clearLayers();
  for (const p of pins) {
    const price = p.price != null ? `${Math.round(p.price).toLocaleString("pl-PL")} zł` : "—";
    const area = p.areaM2 != null ? `${p.areaM2} m²` : "";
    const rooms = p.rooms || "";
    const mode = (p.transaction || "").toUpperCase() === "RENT" ? "Rent" : "Buy";
    const m = L.circleMarker([p.lat, p.lon], {
      radius: 7,
      color: "#c45c26",
      fillColor: "#e67e22",
      fillOpacity: 0.85,
      weight: 2,
    });
    const wrap = document.createElement("div");
    wrap.className = "otodom-popup";
    wrap.innerHTML = `<strong>${escapeHtml(p.title || "")}</strong><br>
      <small>${price}${area ? ` · ${area}` : ""}${rooms ? ` · ${rooms}` : ""} · ${t("otodomApprox")}</small><br>
      <a href="${escapeAttr(p.url)}" target="_blank" rel="noopener noreferrer">${t("otodomOpen")}</a><br>`;
    const saveBtn = document.createElement("button");
    saveBtn.type = "button";
    saveBtn.className = "btn primary";
    saveBtn.textContent = t("otodomSave");
    saveBtn.onclick = () => {
      openOfferDialog({
        title: p.title,
        lat: p.lat,
        lon: p.lon,
        cityId: ctx.getActiveCityId?.() || null,
        url: p.url || "",
        mode,
        price: p.price ?? null,
        sqm: p.areaM2 ?? null,
      });
    };
    wrap.appendChild(saveBtn);
    m.bindPopup(wrap);
    m.bindTooltip(price, { direction: "top" });
    otodomLayer.addLayer(m);
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
