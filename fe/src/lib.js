import { useEffect, useRef, useState } from 'react';

// Same origin as be/Cadflow.Api; when running `npm run dev`, Vite forwards /api to the server.
export const API = '';

export async function api(path, opts) {
  const res = await fetch(API + path, opts);
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) {
    const err = new Error(body?.error || `${res.status} ${res.statusText}`);
    err.body = body;
    throw err;
  }
  return body;
}

export const postJson = (path, data) =>
  api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });

/**
 * Sends the content of a .gea script to the server to compile it; on success it stores the result for the order-from-script page and goes there.
 * A compile error is rethrown (e.body.diagnostics has the list of errors), shared by the Import .gea button and the Order button of the Components page.
 */
export async function importScriptText(text, fileName) {
  const res = await postJson('/api/gea/import', { text });
  sessionStorage.setItem('cadflow.import', JSON.stringify({
    scriptId: res.scriptId, values: res.values, texts: res.texts, warnings: res.warnings, hasValues: res.hasValues, fileName,
  }));
  go(`#/import/${res.scriptId}`);
}

export const fmtTime = (iso) => (iso ? new Date(iso).toLocaleTimeString() : '-');
export const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString() : '-');
export const fmtSize = (n) => (n > 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB');
export const fmtDuration = (s) => (s == null ? '-' : s >= 90 ? `${Math.floor(s / 60)} min ${Math.round(s % 60)} s` : `${Math.round(s)} s`);

/** Display name of a product: a job ordered from an imported script shows the script name. */
export const productLabel = (j) => (j.productCode === 'imported' ? `${j.scriptName || 'script'} (imported)` : j.productCode);

export const STATUS_COLOR = { queued: 'gray', running: 'blue', succeeded: 'green', failed: 'red', cancelled: 'amber' };

/** Calls fn right away and repeats every ms; stops on unmount. fn is always the latest version. */
export function usePolling(fn, ms, deps = []) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => {
    let alive = true;
    const run = () => Promise.resolve(ref.current()).catch(() => {});
    run();
    const t = setInterval(() => alive && run(), ms);
    return () => { alive = false; clearInterval(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ms, ...deps]);
}

/** Hash-based router (#/orders, #/jobs/<id>...): enough for 5 screens, no library needed. */
export function useHash() {
  const [hash, setHash] = useState(location.hash || '#/');
  useEffect(() => {
    const on = () => setHash(location.hash || '#/');
    addEventListener('hashchange', on);
    return () => removeEventListener('hashchange', on);
  }, []);
  return hash;
}

export const go = (hash) => { location.hash = hash; };

/** Reopens an existing job on the New order form (a job built from an imported script reopens the form of that script). */
export function reorder(job) {
  if (job.productCode === 'imported' && job.scriptSha256) {
    sessionStorage.setItem('cadflow.importRetry', JSON.stringify({ scriptId: job.scriptSha256, values: job.inputs, texts: job.textInputs }));
    go(`#/import/${job.scriptSha256}`);
  } else {
    sessionStorage.setItem('cadflow.retry', JSON.stringify(job.inputs));
    go(`#/new/${job.productCode}`);
  }
}
