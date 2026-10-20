// Talks to the admin API. The access token lives only in memory; the refresh token is an HttpOnly cookie
// that JavaScript cannot read, so a script injected into the page can not steal a long-lived credential.

// The console may run under a path prefix (e.g. /d2d/admin/) when D2D is an IIS application.
const prefix = location.pathname.replace(/\/admin(\/.*)?$/, '');
const ROOT = prefix + '/api/v1/admin';

/** Path prefix D2D is hosted under (empty when it owns the site root). */
export const BASE = prefix;

let session = null; // { accessToken, admin: { id, email, displayName, role } }
let refreshing = null;
let signedOut = () => {};

export class ApiError extends Error {
  constructor(status, message, detail) {
    super(detail ? `${message}: ${detail}` : message);
    this.status = status;
  }
}

export const getSession = () => session;
export const canWrite = () => session?.admin?.role === 'Admin';
export const onSignedOut = (fn) => { signedOut = fn; };

async function send(path, { method = 'GET', body, headers = {} } = {}) {
  const h = { ...headers };
  if (body !== undefined) h['Content-Type'] = 'application/json';
  if (session) h.Authorization = 'Bearer ' + session.accessToken;
  return fetch(ROOT + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), credentials: 'same-origin' });
}

async function parse(res) {
  if (res.status === 204) return null;
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) {
    const validation = data?.errors ? Object.values(data.errors).flat().join(' ') : null;
    throw new ApiError(res.status, validation || data?.title || res.statusText || 'Request failed', validation ? null : data?.detail);
  }
  return data;
}

async function request(path, options) {
  let res = await send(path, options);
  if (res.status === 401 && session && (await refresh())) res = await send(path, options);
  if (res.status === 401 && session) {
    session = null;
    signedOut();
  }
  return parse(res);
}

export const api = {
  get: (path) => request(path),
  post: (path, body) => request(path, { method: 'POST', body }),
  put: (path, body) => request(path, { method: 'PUT', body }),
  del: (path) => request(path, { method: 'DELETE' }),
};

/** Uses the session cookie to get a new access token. Several callers share one request. */
export function refresh() {
  refreshing ??= (async () => {
    try {
      const res = await fetch(ROOT + '/auth/refresh', { method: 'POST', headers: { 'X-Requested-With': 'd2d-admin' }, credentials: 'same-origin' });
      if (!res.ok) return false;
      session = await res.json();
      return true;
    } catch {
      return false;
    } finally {
      refreshing = null;
    }
  })();
  return refreshing;
}

export async function login(email, password) {
  const res = await fetch(ROOT + '/auth/login', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
    body: JSON.stringify({ email, password }),
  });
  session = await parse(res);
}

export async function logout() {
  try {
    await fetch(ROOT + '/auth/logout', { method: 'POST', headers: { 'X-Requested-With': 'd2d-admin' }, credentials: 'same-origin' });
  } finally {
    session = null;
  }
}

export async function setupRequired() {
  try {
    const res = await fetch(ROOT + '/auth/status', { credentials: 'same-origin' });
    return res.ok && (await res.json()).setupRequired === true;
  } catch {
    return false;
  }
}

/** First-run: creates the first admin. Proof of ownership is the static admin key, which is used once and not kept. */
export async function createFirstAdmin(adminKey, { email, displayName, password }) {
  const res = await fetch(ROOT + '/admins', {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Admin-Key': adminKey },
    body: JSON.stringify({ email, displayName, password, role: 'Admin' }),
  });
  if (res.status === 401) throw new ApiError(401, 'The admin key is wrong');
  return parse(res);
}

export async function changePassword(currentPassword, newPassword) {
  await api.post('/auth/password', { currentPassword, newPassword });
  session = null;
}
