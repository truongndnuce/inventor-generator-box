import { useEffect, useRef, useState } from 'react';

// Cùng origin với be/Cadflow.Api; khi chạy `npm run dev`, Vite chuyển tiếp /api sang server.
export const API = '';

export async function api(path, opts) {
  const res = await fetch(API + path, opts);
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* không phải JSON */ }
  if (!res.ok) {
    const err = new Error(body?.error || `${res.status} ${res.statusText}`);
    err.body = body;
    throw err;
  }
  return body;
}

export const postJson = (path, data) =>
  api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });

export const fmtTime = (iso) => (iso ? new Date(iso).toLocaleTimeString() : '-');
export const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString() : '-');
export const fmtSize = (n) => (n > 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB');
export const fmtDuration = (s) => (s == null ? '-' : s >= 90 ? `${Math.floor(s / 60)} min ${Math.round(s % 60)} s` : `${Math.round(s)} s`);

/** Tên sản phẩm hiển thị: job đặt từ script import ghi rõ tên script. */
export const productLabel = (j) => (j.productCode === 'imported' ? `${j.scriptName || 'script'} (imported)` : j.productCode);

export const STATUS_COLOR = { queued: 'gray', running: 'blue', succeeded: 'green', failed: 'red', cancelled: 'amber' };

/** Gọi fn ngay và lặp lại mỗi ms; dừng khi unmount. fn luôn là bản mới nhất. */
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

/** Router theo hash (#/orders, #/jobs/<id>...): đủ cho 5 màn hình, không cần thư viện. */
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

/** Mở lại một job đã có trên form New order (job dựng từ script nhập vào thì mở lại form của script đó). */
export function reorder(job) {
  if (job.productCode === 'imported' && job.scriptSha256) {
    sessionStorage.setItem('cadflow.importRetry', JSON.stringify({ scriptId: job.scriptSha256, values: job.inputs, texts: job.textInputs }));
    go(`#/import/${job.scriptSha256}`);
  } else {
    sessionStorage.setItem('cadflow.retry', JSON.stringify(job.inputs));
    go(`#/new/${job.productCode}`);
  }
}
