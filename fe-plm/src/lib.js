import { useEffect, useState } from 'react';

// Same origin as be/Cadflow.Api; when running `npm run dev`, Vite forwards /api to the server.
export async function api(path, opts) {
  const res = await fetch(path, opts);
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) throw new Error(body?.error || `${res.status} ${res.statusText}`);
  return body;
}

export const send = (method, path, data) =>
  api(path, { method, headers: { 'Content-Type': 'application/json' }, body: data === undefined ? undefined : JSON.stringify(data) });

export const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '-');
export const fmtSize = (n) => (n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB');

/** Where the main Cadflow app (orders, jobs) lives; set VITE_MAIN_APP_URL when running both dev servers. */
export const MAIN_APP = import.meta.env.VITE_MAIN_APP_URL || '/';

export const STATE_COLOR = { 'In Work': 'gray', 'Under Review': 'amber', Approved: 'green', Released: 'blue' };

/** Hash router: #/files, #/files/<folderId>, #/item/<id>. */
export function useHash() {
  const [hash, setHash] = useState(location.hash || '#/files');
  useEffect(() => {
    const on = () => setHash(location.hash || '#/files');
    addEventListener('hashchange', on);
    return () => removeEventListener('hashchange', on);
  }, []);
  return hash;
}
export const go = (h) => { location.hash = h; };

/** The user name (no login yet): typed once, kept in this browser, sent as "by" with every change. */
export function useUser() {
  const [user, setUser] = useState(() => { try { return localStorage.getItem('plm.user') || ''; } catch { return ''; } });
  const save = (v) => { setUser(v); try { localStorage.setItem('plm.user', v); } catch { /* ignore */ } };
  return [user, save];
}

export const fileUrl = (itemId, version, download) =>
  `/api/vault/items/${itemId}/versions/${version}/file${download ? '?download=true' : ''}`;

/** Uploads one file as the body of a POST (a same-name file becomes a new version). */
export async function uploadFile(url, file) {
  const res = await fetch(url, { method: 'POST', body: file });
  const body = await res.json().catch(() => null);
  if (!res.ok) throw new Error(body?.error || `${res.status} ${res.statusText}`);
  return body;
}
