const TOKEN_KEY = "cc_id_token";

export function getToken() {
  return sessionStorage.getItem(TOKEN_KEY);
}

export function setToken(token) {
  sessionStorage.setItem(TOKEN_KEY, token);
}

export function clearToken() {
  sessionStorage.removeItem(TOKEN_KEY);
}

/** Decode JWT payload without verifying signature (expiry check only). */
export function isTokenExpired(token, skewSeconds = 60) {
  try {
    const payload = JSON.parse(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
    if (!payload.exp) return false;
    return Date.now() / 1000 >= payload.exp - skewSeconds;
  } catch {
    return true;
  }
}

export async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (options.body && !headers["Content-Type"]) {
    headers["Content-Type"] = "application/json";
  }
  const token = getToken();
  if (token) {
    if (isTokenExpired(token)) {
      clearToken();
      const err = new Error("session expired");
      err.status = 401;
      throw err;
    }
    headers.Authorization = `Bearer ${token}`;
  }

  const res = await fetch(path, { ...options, headers });
  if (!res.ok) {
    let body = null;
    try {
      body = await res.json();
    } catch { /* ignore */ }
    if (res.status === 401) clearToken();
    const err = new Error(body?.error || res.statusText || "request failed");
    err.status = res.status;
    err.body = body;
    throw err;
  }
  if (res.status === 204) return null;
  return res.json();
}
