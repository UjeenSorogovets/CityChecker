import { api, getToken } from "./api.js";
import { t } from "./i18n.js";

/** @type {L.LayerGroup | null} */
let anchorLayer = null;
/** @type {L.LayerGroup | null} */
let offerLayer = null;
/** @type {L.LayerGroup | null} */
let otodomLayer = null;
/** @type {{ map: L.Map, getActiveCityId: () => string|null, getContext: () => any, onPlaceClickForAnchor?: boolean }} */
let ctx = null;

let placingAnchor = false;
let filterState = {
  shortlistOnly: false,
  minScore: 0,
  maxCommute: 0,
  hasOffers: false,
};

const OTODOM_FILTERS_KEY = "cc_otodom_filters";
const OTODOM_DEFAULTS = { priceMax: 650000, areaMin: 50, rooms: ["TWO", "THREE", "FOUR", "FIVE", "SIX_OR_MORE"] };
let otodomEnabled = false;
let otodomTimer = null;
let otodomGen = 0;

export function initHousing(options) {
  ctx = options;
  anchorLayer = L.layerGroup().addTo(ctx.map);
  offerLayer = L.layerGroup().addTo(ctx.map);
  otodomLayer = L.layerGroup().addTo(ctx.map);
  wireUi();
  refreshAnchors();
  refreshOffers();
}

export function housingMapClick(latlng) {
  if (!placingAnchor) return false;
  placingAnchor = false;
  document.getElementById("housing-place-anchor")?.classList.remove("active");
  const label = prompt(t("anchorLabelPrompt"), "Work");
  if (!label) return true;
  api("/api/housing/anchors", {
    method: "POST",
    body: JSON.stringify({ label, lat: latlng.lat, lon: latlng.lng }),
  }).then(() => refreshAnchors()).catch(alertErr);
  return true;
}

export function getHousingFilters() {
  return filterState;
}

export async function enrichDistrictSheet(districtId, container) {
  if (!container) return;
  container.innerHTML = "";
  const bar = document.createElement("div");
  bar.className = "housing-district-actions";
  bar.innerHTML = `
    <button type="button" class="btn ghost" data-act="shortlist">${t("shortlist")}</button>
    <button type="button" class="btn ghost" data-act="veto">${t("veto")}</button>
    <button type="button" class="btn ghost" data-act="probe">${t("probeAmenities")}</button>
    <button type="button" class="btn ghost" data-act="visit">${t("logVisit")}</button>
    <button type="button" class="btn ghost" data-act="offer">${t("addOfferHere")}</button>
    <button type="button" class="btn ghost" data-act="reminder">${t("setReminder")}</button>
    <button type="button" class="btn ghost" data-act="risk">${t("riskNotes")}</button>
  `;
  bar.querySelector("[data-act=shortlist]").onclick = () => setPick(districtId, "Shortlist");
  bar.querySelector("[data-act=veto]").onclick = () => {
    const reason = prompt(t("vetoReasonPrompt"), "");
    setPick(districtId, "Veto", reason || "");
  };
  bar.querySelector("[data-act=probe]").onclick = async () => {
    try {
      const p = await api(`/api/housing/picks/${districtId}/probe`, { method: "POST" });
      alert(`${t("probeAmenities")}: parks ${p.parkCount}, shops ${p.shopCount}, quiet ${p.quietScore ?? "—"}, hwy ${p.nearestHighwayKm ?? "—"} km`);
    } catch (e) { alertErr(e); }
  };
  bar.querySelector("[data-act=visit]").onclick = () => openVisitDialog(districtId);
  bar.querySelector("[data-act=offer]").onclick = () =>
    openOfferAt({ districtId, title: ctx.getContext?.()?.title });
  bar.querySelector("[data-act=reminder]").onclick = async () => {
    const when = prompt(t("reminderWhen"), new Date(Date.now() + 86400000).toISOString().slice(0, 16));
    if (!when) return;
    const note = prompt(t("reminderNote"), "Revisit evening") || "";
    try {
      const picks = await api("/api/housing/picks");
      const cur = picks.find((p) => p.districtId === districtId);
      await api(`/api/housing/picks/${districtId}`, {
        method: "PUT",
        body: JSON.stringify({
          status: cur?.status ?? "Exploring",
          vetoReason: cur?.vetoReason ?? null,
          quietScore: cur?.quietScore ?? null,
          riskNotes: cur?.riskNotes ?? null,
          reminderAt: new Date(when).toISOString(),
          reminderNote: note,
        }),
      });
      alert(t("reminderSaved"));
    } catch (e) { alertErr(e); }
  };
  bar.querySelector("[data-act=risk]").onclick = async () => {
    const picks = await api("/api/housing/picks");
    const cur = picks.find((p) => p.districtId === districtId);
    const risk = prompt(t("riskNotesPrompt"), cur?.riskNotes || "") || "";
    try {
      await api(`/api/housing/picks/${districtId}`, {
        method: "PUT",
        body: JSON.stringify({
          status: cur?.status ?? "Exploring",
          vetoReason: cur?.vetoReason ?? null,
          quietScore: cur?.quietScore ?? null,
          reminderAt: cur?.reminderAt ?? null,
          reminderNote: cur?.reminderNote ?? null,
          riskNotes: risk,
        }),
      });
      alert(t("riskSaved"));
    } catch (e) { alertErr(e); }
  };
  container.appendChild(bar);
}

function wireUi() {
  const panel = document.getElementById("housing-panel");
  const toggle = document.getElementById("housing-toggle");
  toggle?.addEventListener("click", () => panel?.classList.toggle("open"));

  panel?.querySelectorAll("[data-htab]").forEach((btn) => {
    btn.addEventListener("click", () => {
      panel.querySelectorAll("[data-htab]").forEach((b) => b.classList.remove("active"));
      panel.querySelectorAll("[data-hpanel]").forEach((p) => p.classList.add("hidden"));
      btn.classList.add("active");
      panel.querySelector(`[data-hpanel="${btn.dataset.htab}"]`)?.classList.remove("hidden");
      if (btn.dataset.htab === "compare") loadCompare();
      if (btn.dataset.htab === "finalists") loadFinalists();
      if (btn.dataset.htab === "weights") loadProfile();
      if (btn.dataset.htab === "offers") refreshOffersList();
    });
  });

  document.getElementById("housing-place-anchor")?.addEventListener("click", (e) => {
    placingAnchor = !placingAnchor;
    e.currentTarget.classList.toggle("active", placingAnchor);
  });

  document.getElementById("housing-refresh-compare")?.addEventListener("click", () => loadCompare());
  document.getElementById("housing-export")?.addEventListener("click", exportCsv);
  document.getElementById("housing-save-weights")?.addEventListener("click", saveProfile);
  document.getElementById("housing-add-offer")?.addEventListener("click", () => openOfferDialog({}));

  const otodomShow = document.getElementById("otodom-show");
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
  otodomShow?.addEventListener("change", (e) => {
    otodomEnabled = !!e.target.checked;
    if (!otodomEnabled) {
      otodomLayer?.clearLayers();
      setOtodomStatus(t("otodomHint"));
      return;
    }
    scheduleOtodomReload(true);
  });
  ctx.map.on("moveend", () => {
    if (otodomEnabled) scheduleOtodomReload();
  });

  document.getElementById("filter-shortlist")?.addEventListener("change", async (e) => {
    filterState.shortlistOnly = e.target.checked;
    await syncMapFilter();
  });
  document.getElementById("filter-minscore")?.addEventListener("change", (e) => {
    filterState.minScore = Number(e.target.value) || 0;
  });
  document.getElementById("filter-maxcommute")?.addEventListener("change", (e) => {
    filterState.maxCommute = Number(e.target.value) || 0;
  });
  document.getElementById("filter-has-offers")?.addEventListener("change", async (e) => {
    filterState.hasOffers = e.target.checked;
    await refreshOffers();
  });
}

async function syncMapFilter() {
  if (!ctx?.onMapFilterChange) return;
  if (!filterState.shortlistOnly) {
    ctx.onMapFilterChange(null);
    return;
  }
  const cityId = ctx.getActiveCityId?.();
  const picks = await api(`/api/housing/picks${cityId ? `?cityId=${cityId}` : ""}`);
  const ids = picks.filter((p) => p.status === "Shortlist").map((p) => p.districtId);
  ctx.onMapFilterChange(ids);
}

async function refreshAnchors() {
  if (!anchorLayer) return;
  anchorLayer.clearLayers();
  const list = await api("/api/housing/anchors");
  const ul = document.getElementById("housing-anchors-list");
  if (ul) {
    ul.innerHTML = "";
    for (const a of list) {
      const li = document.createElement("li");
      li.innerHTML = `<span>${a.label}</span> <button type="button" class="btn ghost danger">×</button>`;
      li.querySelector("button").onclick = async () => {
        await api(`/api/housing/anchors/${a.anchorId}`, { method: "DELETE" });
        refreshAnchors();
      };
      ul.appendChild(li);
      const m = L.circleMarker([a.lat, a.lon], {
        radius: 7, color: "#b33a3a", fillColor: "#b33a3a", fillOpacity: 0.9, weight: 2,
      });
      m.bindTooltip(a.label);
      anchorLayer.addLayer(m);
    }
  }
}

async function setPick(districtId, status, vetoReason = null) {
  try {
    await api(`/api/housing/picks/${districtId}`, {
      method: "PUT",
      body: JSON.stringify({ status, vetoReason }),
    });
  } catch (e) { alertErr(e); }
}

async function loadCompare() {
  const cityId = ctx.getActiveCityId();
  const el = document.getElementById("housing-compare");
  if (!el) return;
  if (!cityId) {
    el.innerHTML = `<p class="muted">${t("zoomIntoCityFirst")}</p>`;
    return;
  }
  el.innerHTML = `<p class="muted">${t("loading")}</p>`;
  try {
    const rows = await api(`/api/housing/compare/${cityId}`);
    let filtered = rows.filter((r) => r.status !== "Veto");
    if (filterState.shortlistOnly) filtered = filtered.filter((r) => r.status === "Shortlist");
    if (filterState.minScore > 0) filtered = filtered.filter((r) => (r.comfortAvg ?? 0) >= filterState.minScore);
    if (filterState.maxCommute > 0) filtered = filtered.filter((r) => r.worstCommuteMin == null || r.worstCommuteMin <= filterState.maxCommute);

    if (!filtered.length) {
      el.innerHTML = `<p class="muted">${t("noCompareRows")}</p>`;
      return;
    }
    const table = document.createElement("table");
    table.className = "housing-table";
    table.innerHTML = `<thead><tr>
      <th>${t("name")}</th><th>${t("status")}</th><th>${t("rank")}</th>
      <th>${t("comfort")}</th><th>${t("commute")}</th><th>${t("quiet")}</th>
      <th>${t("parks")}</th><th>${t("visits")}</th>
    </tr></thead>`;
    const tbody = document.createElement("tbody");
    for (const r of filtered) {
      const tr = document.createElement("tr");
      tr.innerHTML = `<td>${r.name}</td><td>${r.status}</td><td>${r.rankScore ?? "—"}</td>
        <td>${fmt(r.comfortAvg)}</td><td>${fmt(r.worstCommuteMin)}'</td><td>${r.quietScore ?? "—"}</td>
        <td>${r.parkCount ?? "—"}</td><td>${r.visitCount}</td>`;
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    el.innerHTML = "";
    el.appendChild(table);
  } catch (e) {
    el.textContent = e.message || t("authFailed");
  }
}

function openVisitDialog(districtId) {
  const evening = numPrompt(t("visitEvening"), 7);
  const daylight = numPrompt(t("visitDaylight"), 7);
  const dog = numPrompt(t("visitDogWalk"), 7);
  const sat = numPrompt(t("visitSaturday"), 7);
  const winter = numPrompt(t("visitWinter"), 5);
  const notes = prompt(t("visitNotes"), "") || "";
  api("/api/housing/visits", {
    method: "POST",
    body: JSON.stringify({
      districtId,
      eveningFeel: evening,
      daylight,
      dogWalk: dog,
      saturdayLife: sat,
      winterFeel: winter,
      notes,
    }),
  }).then(() => alert(t("visitSaved"))).catch(alertErr);
}

export function openOfferAt({ lat, lon, cityId, districtId, buildingId, title }) {
  openOfferDialog({ lat, lon, districtId, title, cityId, buildingId });
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
    if (filterState.hasOffers && !o.url && o.price == null) continue;
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

async function loadFinalists() {
  const el = document.getElementById("housing-finalists");
  if (!el) return;
  const rows = await api("/api/housing/finalists");
  if (!rows.length) {
    el.innerHTML = `<p class="muted">${t("noFinalists")}</p>`;
    return;
  }
  const table = document.createElement("table");
  table.className = "housing-table";
  table.innerHTML = `<thead><tr><th>${t("name")}</th><th>${t("mode")}</th><th>${t("monthlyTotal")}</th><th>${t("dealAvg")}</th><th>${t("offerKillerFlaw")}</th></tr></thead>`;
  const tb = document.createElement("tbody");
  for (const r of rows) {
    const o = r.offer;
    const tr = document.createElement("tr");
    tr.innerHTML = `<td>${o.title}</td><td>${o.mode}</td><td>${r.monthlyTotal ?? "—"}</td><td>${r.dealAvg ?? "—"}</td><td>${o.killerFlaw || "—"}</td>`;
    tb.appendChild(tr);
  }
  table.appendChild(tb);
  el.innerHTML = "";
  el.appendChild(table);
}

async function loadProfile() {
  const p = await api("/api/housing/profile");
  document.getElementById("w-commute").value = p.weightCommute;
  document.getElementById("w-quiet").value = p.weightQuiet;
  document.getElementById("w-price").value = p.weightPrice;
  document.getElementById("w-green").value = p.weightGreen;
  document.getElementById("w-comfort").value = p.weightComfort;
}

async function saveProfile() {
  const body = {
    weightCommute: Number(document.getElementById("w-commute").value) || 0,
    weightQuiet: Number(document.getElementById("w-quiet").value) || 0,
    weightPrice: Number(document.getElementById("w-price").value) || 0,
    weightGreen: Number(document.getElementById("w-green").value) || 0,
    weightComfort: Number(document.getElementById("w-comfort").value) || 0,
  };
  await api("/api/housing/profile", { method: "PUT", body: JSON.stringify(body) });
  alert(t("weightsSaved"));
}

async function exportCsv() {
  const token = getToken();
  const res = await fetch("/api/housing/export.csv", {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  });
  if (!res.ok) { alert(t("authFailed")); return; }
  const blob = await res.blob();
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = "citychecker-export.csv";
  a.click();
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

function scheduleOtodomReload(immediate = false) {
  clearTimeout(otodomTimer);
  otodomTimer = setTimeout(() => reloadOtodomPins(), immediate ? 0 : 450);
}

async function reloadOtodomPins() {
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

  const b = ctx.map.getBounds();
  const gen = ++otodomGen;
  setOtodomStatus(t("otodomLoading"));
  try {
    const res = await api("/api/housing/otodom/pins", {
      method: "POST",
      body: JSON.stringify({
        cityId,
        priceMax: filters.priceMax,
        areaMin: filters.areaMin,
        rooms: filters.rooms,
        transaction: "SELL",
        west: b.getWest(),
        south: b.getSouth(),
        east: b.getEast(),
        north: b.getNorth(),
      }),
    });
    if (gen !== otodomGen) return;
    if (!res.ok) {
      otodomLayer.clearLayers();
      setOtodomStatus(res.error || t("otodomEmpty"));
      return;
    }
    renderOtodomPins(res.pins || []);
    const n = (res.pins || []).length;
    const total = res.totalMatched;
    const listed = res.listed;
    if (!n) {
      setOtodomStatus(t("otodomEmpty"));
    } else if (total != null) {
      setOtodomStatus(`${t("otodomLoaded")}: ${n} · ${t("otodomMatched")}: ${total}${listed != null && listed < total ? ` (${t("otodomListed")}: ${listed})` : ""}`);
    } else {
      setOtodomStatus(`${t("otodomLoaded")}: ${n}`);
    }
  } catch (e) {
    if (gen !== otodomGen) return;
    otodomLayer.clearLayers();
    setOtodomStatus(e.message || t("otodomEmpty"));
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

function fmt(n) {
  return n == null ? "—" : (typeof n === "number" ? n.toFixed(1) : n);
}

function alertErr(e) {
  alert(e?.body?.error || e?.message || String(e));
}
