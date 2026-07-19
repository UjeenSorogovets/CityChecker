export const i18n = {
  en: {
    appTitle: "Poland City Comfort Mapper",
    signInHint: "Sign in with Google to manage your city comfort notes.",
    signOut: "Sign out",
    selectPlace: "Select a place on the map",
    addNote: "Add note",
    editNote: "Edit note",
    noteText: "Note",
    scoreOverall: "Overall (1–10)",
    scoreNature: "Nature",
    scoreShops: "Shops",
    scoreTransport: "Transport",
    scoreSafety: "Safety",
    cancel: "Cancel",
    save: "Save",
    delete: "Delete",
    modeCity: "City level",
    modeDistrict: "District level",
    modeBuilding: "Building level",
    noNotes: "No notes yet.",
    avgScore: "Average score",
    loading: "Loading…",
    authFailed: "Sign-in failed or your account is not allowed.",
    authSubHint: "Copy this into GOOGLE_ALLOWED_USER_ID in .env, then restart docker-compose:",
    geocodeFail: "Could not resolve address here.",
  },
  ru: {
    appTitle: "Карта комфорта городов Польши",
    signInHint: "Войдите через Google, чтобы вести заметки о комфорте городов.",
    signOut: "Выйти",
    selectPlace: "Выберите место на карте",
    addNote: "Добавить заметку",
    editNote: "Изменить заметку",
    noteText: "Заметка",
    scoreOverall: "Общая оценка (1–10)",
    scoreNature: "Природа",
    scoreShops: "Магазины",
    scoreTransport: "Транспорт",
    scoreSafety: "Безопасность",
    cancel: "Отмена",
    save: "Сохранить",
    delete: "Удалить",
    modeCity: "Уровень города",
    modeDistrict: "Уровень района",
    modeBuilding: "Уровень здания",
    noNotes: "Заметок пока нет.",
    avgScore: "Средняя оценка",
    loading: "Загрузка…",
    authFailed: "Вход не выполнен или аккаунт не разрешён.",
    authSubHint: "Скопируйте это в GOOGLE_ALLOWED_USER_ID в .env и перезапустите docker-compose:",
    geocodeFail: "Не удалось определить адрес.",
  },
};

let lang = localStorage.getItem("cc_lang") || "en";

export function getLang() {
  return lang;
}

export function t(key) {
  return i18n[lang][key] ?? i18n.en[key] ?? key;
}

export function setLang(next) {
  lang = next === "ru" ? "ru" : "en";
  localStorage.setItem("cc_lang", lang);
  applyI18n();
  return lang;
}

export function toggleLang() {
  return setLang(lang === "en" ? "ru" : "en");
}

export function applyI18n() {
  document.querySelectorAll("[data-i18n]").forEach((el) => {
    const key = el.getAttribute("data-i18n");
    el.textContent = t(key);
  });
  const btn = document.getElementById("lang-toggle");
  if (btn) btn.textContent = lang.toUpperCase();
}
