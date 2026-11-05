// CADFlow front-end: không cần bước build. Form sinh từ schema của server; luật chạy ở server (một nguồn chuẩn).
const API = ''; // cùng origin với be/Cadflow.Api; đặt 'http://localhost:18080' nếu chạy front-end ở máy khác.

const view = document.getElementById('view');
let timers = [];

// ---------------------------------------------------------------- tiện ích

/** Tạo phần tử DOM; nội dung luôn đi qua textContent nên dữ liệu người dùng nhập không thể chèn HTML. */
function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === false || v == null) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) if (c != null && c !== false) el.append(c.nodeType ? c : document.createTextNode(String(c)));
  return el;
}

async function api(path, opts) {
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

const postJson = (path, data) =>
  api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });

const fmtTime = (iso) => (iso ? new Date(iso).toLocaleTimeString() : '-');
const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString() : '-');
const fmtSize = (n) => (n > 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB');
const statusBadge = (s) => h('span', { class: `status s-${s}` }, s);

function every(ms, fn) {
  const t = setInterval(fn, ms);
  timers.push(t);
}

function clearTimers() {
  timers.forEach(clearInterval);
  timers = [];
}

// ---------------------------------------------------------------- trạng thái worker

async function refreshWorkerPill() {
  const pill = document.getElementById('worker-pill');
  try {
    const workers = await api('/api/workers');
    const online = workers.filter((w) => w.online);
    if (online.length) {
      pill.className = 'pill pill-on';
      pill.textContent = `worker online: ${online.map((w) => w.name).join(', ')}`;
    } else {
      pill.className = 'pill pill-off';
      pill.textContent = 'no Inventor worker online';
    }
  } catch {
    pill.className = 'pill pill-off';
    pill.textContent = 'server unreachable';
  }
}

// ---------------------------------------------------------------- Configurator

async function renderConfigurator(code) {
  view.replaceChildren(h('p', { class: 'muted' }, 'Loading products...'));
  const products = await api('/api/products');
  const product = products.find((p) => p.code === code) || products[0];
  const picker = h('div', { class: 'tabs' }, products.map((p) =>
    h('a', { href: `#/new/${p.code}`, class: p.code === product.code ? 'tab active' : 'tab' }, p.name)));
  const schema = await api(`/api/products/${product.code}`);

  const values = { ...schema.defaults };
  // "Edit and resubmit" từ một job lỗi: nạp lại đầu vào cũ (chỉ lấy khóa có trong schema).
  const retry = sessionStorage.getItem('cadflow.retry');
  sessionStorage.removeItem('cadflow.retry');
  if (retry) {
    try { for (const [k, v] of Object.entries(JSON.parse(retry))) if (k in values && Number.isFinite(v)) values[k] = v; } catch { /* bỏ qua */ }
  }
  const rows = new Map(); // tên thông số -> { field, number, range }
  const issuesBox = h('ul', { class: 'issues', hidden: true });
  const submit = h('button', { class: 'primary', type: 'button' }, 'Submit order');
  const who = h('input', { type: 'text', placeholder: 'Requested by', value: localStorage.getItem('cadflow.user') || '' });
  const note = h('div', { class: 'notice', hidden: true });

  const form = h('div', {},
    schema.groups.map((g) =>
      h('fieldset', {}, h('legend', {}, g),
        schema.parameters.filter((p) => p.group === g).map((p) => {
          const number = h('input', { type: 'number', min: p.min, max: p.max, step: p.integer ? 1 : 'any', value: values[p.name], id: p.name });
          const range = h('input', { type: 'range', min: p.min, max: p.max, step: p.integer || p.max - p.min > 50 ? 1 : 0.1, value: values[p.name] });
          const sync = (v, src) => {
            values[p.name] = v;
            if (src !== number) number.value = v;
            if (src !== range) range.value = v;
            scheduleValidate();
          };
          number.addEventListener('input', () => number.value !== '' && sync(Number(number.value), number));
          range.addEventListener('input', () => sync(Number(range.value), range));
          const field = h('div', { class: 'field' },
            h('label', { for: p.name }, p.label), number, range, h('span', { class: 'range' }, `${p.min}-${p.max} ${p.unit}`.trim()));
          rows.set(p.name, { field });
          return field;
        }))));

  let seq = 0;
  let valid = false;
  let timer;
  function scheduleValidate() {
    submit.disabled = true;
    clearTimeout(timer);
    timer = setTimeout(validate, 250);
  }
  async function validate() {
    const mine = ++seq;
    try {
      const r = await postJson(`/api/products/${product.code}/validate`, { values });
      if (mine !== seq) return; // đã có lần gõ mới hơn
      valid = r.valid;
      const bad = new Set(r.issues.flatMap((i) => i.params));
      rows.forEach(({ field }, name) => field.classList.toggle('invalid', bad.has(name)));
      issuesBox.hidden = r.valid;
      issuesBox.replaceChildren(...r.issues.map((i) => h('li', {}, i.message)));
      submit.disabled = !valid;
    } catch (e) {
      note.hidden = false;
      note.textContent = 'Could not validate: ' + e.message;
    }
  }

  submit.addEventListener('click', async () => {
    submit.disabled = true;
    try {
      localStorage.setItem('cadflow.user', who.value);
      const job = await postJson('/api/jobs', { productCode: product.code, inputs: values, requestedBy: who.value });
      location.hash = `#/jobs/${job.id}`;
    } catch (e) {
      note.hidden = false;
      note.textContent = 'Order rejected: ' + (e.body?.issues?.[0]?.message || e.message);
      submit.disabled = false;
    }
  });

  view.replaceChildren(
    h('div', { class: 'card' },
      picker,
      h('h1', {}, product.name),
      h('p', { class: 'muted' }, `${product.description} Rules are checked by the server; the same rules run again when the worker builds.`),
      note, form, issuesBox,
      h('div', { class: 'row' }, who, submit)));
  validate();
}

// ---------------------------------------------------------------- Orders

async function renderOrders() {
  const body = h('tbody');
  view.replaceChildren(h('div', { class: 'card' }, h('h1', {}, 'Orders'),
    h('table', {}, h('thead', {}, h('tr', {}, ['Job', 'Product', 'Status', 'Created', 'Requested by', 'Worker'].map((t) => h('th', {}, t)))), body)));

  async function load() {
    const jobs = await api('/api/jobs');
    body.replaceChildren(...(jobs.length
      ? jobs.map((j) => h('tr', {},
        h('td', {}, h('a', { href: `#/jobs/${j.id}` }, j.id)), h('td', {}, j.productCode),
        h('td', {}, statusBadge(j.status), j.step && j.status === 'running' ? ` ${j.step}` : ''),
        h('td', {}, fmtDate(j.createdAt)), h('td', {}, j.requestedBy), h('td', {}, j.worker || '-')))
      : [h('tr', {}, h('td', { colspan: 6, class: 'muted' }, 'No orders yet.'))]));
  }
  await load();
  every(3000, () => load().catch(() => {}));
}

// ---------------------------------------------------------------- Job

const VIEW_LABELS = { front: 'Front', top: 'Top', right: 'Right' };
/** Tên góc nhìn từ tên file: <id>_front.png -> Front; <id>.png (không hậu tố) là góc iso. */
const viewLabel = (name) => VIEW_LABELS[(name.match(/_([a-z]+)\.png$/) || [])[1]] || 'Isometric';

/** Thư viện ảnh: ảnh lớn + dải thumbnail; bấm thumbnail để đổi, bấm ảnh lớn để mở bản đầy đủ ở tab mới. */
function gallery(images, url) {
  if (!images.length) return null;
  const sorted = [...images].sort((a, b) => (viewLabel(a.name) === 'Isometric' ? -1 : viewLabel(b.name) === 'Isometric' ? 1 : a.name.localeCompare(b.name)));
  const big = h('img', { class: 'picture', src: url(sorted[0]), alt: viewLabel(sorted[0].name) });
  const bigLink = h('a', { href: url(sorted[0]), target: '_blank', rel: 'noopener' }, big);
  const caption = h('div', { class: 'muted' }, viewLabel(sorted[0].name));
  const thumbs = sorted.map((a, i) => {
    const t = h('button', { type: 'button', class: i === 0 ? 'thumb active' : 'thumb', title: viewLabel(a.name) },
      h('img', { src: url(a), alt: viewLabel(a.name) }), h('span', {}, viewLabel(a.name)));
    t.addEventListener('click', () => {
      big.src = url(a); big.alt = viewLabel(a.name); bigLink.href = url(a); caption.textContent = viewLabel(a.name);
      thumbs.forEach((x) => x.classList.toggle('active', x === t));
    });
    return t;
  });
  return h('div', { class: 'gallery' }, bigLink, caption, h('div', { class: 'thumbs' }, thumbs));
}

async function renderJob(id) {
  const root = h('div');
  view.replaceChildren(root);

  async function load() {
    const job = await api(`/api/jobs/${encodeURIComponent(id)}`);
    const done = ['succeeded', 'failed', 'cancelled'].includes(job.status);

    const message = {
      queued: 'Waiting for an Inventor worker to pick up the job...',
      running: `The worker is building: ${job.step || '...'}`,
      succeeded: 'Build finished.',
      failed: 'The build failed: ' + (job.error || 'unknown error'),
      cancelled: 'This order was cancelled.',
    }[job.status];

    const pdf = job.artifacts.find((a) => a.kind === 'pdf');
    const url = (a) => `${API}/api/jobs/${encodeURIComponent(job.id)}/artifacts/${encodeURIComponent(a.name)}`;

    root.replaceChildren(
      h('div', { class: 'card' },
        h('div', { class: 'row' }, h('h1', {}, job.id), statusBadge(job.status),
          !done && h('button', { onclick: async () => { await api(`/api/jobs/${job.id}/cancel`, { method: 'POST' }).catch(() => {}); load(); } }, 'Cancel'),
          job.status === 'failed' && h('button', { onclick: () => { sessionStorage.setItem('cadflow.retry', JSON.stringify(job.inputs)); location.hash = `#/new/${job.productCode}`; } }, 'Edit and resubmit')),
        h('p', {}, message),
        h('div', { class: 'cols' },
          h('dl', { class: 'meta' },
            h('dt', {}, 'Product'), h('dd', {}, `${job.productCode} v${job.productVersion}`),
            h('dt', {}, 'Requested by'), h('dd', {}, job.requestedBy),
            h('dt', {}, 'Created'), h('dd', {}, fmtDate(job.createdAt)),
            h('dt', {}, 'Started'), h('dd', {}, fmtDate(job.startedAt)),
            h('dt', {}, 'Finished'), h('dd', {}, fmtDate(job.finishedAt)),
            h('dt', {}, 'Worker'), h('dd', {}, job.worker || '-'),
            h('dt', {}, 'Attempts'), h('dd', {}, job.attempts),
            Object.keys(job.summary).length ? [h('dt', {}, 'Summary'), h('dd', {},
              Object.entries(job.summary).map(([k, v]) => `${k}: ${v}`).join('   '))] : null),
          h('dl', { class: 'meta' }, Object.entries(job.inputs).flatMap(([k, v]) => [h('dt', {}, k), h('dd', {}, /Count$/.test(k) ? String(v) : `${v} mm`)])))),
      job.status === 'succeeded' && h('div', { class: 'card' },
        h('h2', {}, 'Result'),
        gallery(job.artifacts.filter((a) => a.kind === 'image'), url),
        h('div', { class: 'downloads' }, job.artifacts.map((a) => h('a', { href: url(a), download: a.name }, `${a.name} (${fmtSize(a.size)})`)))),
      job.status === 'succeeded' && pdf && h('div', { class: 'card' },
        h('h2', {}, '2D drawing'),
        h('p', { class: 'muted' }, 'ISO 128/129 layout: first-angle projection, section A-A, overall and section dimensions.'),
        h('embed', { class: 'pdf', src: url(pdf) + '#view=FitH', type: 'application/pdf' })),
      h('div', { class: 'card' }, h('h2', {}, 'Timeline'),
        h('ul', { class: 'timeline' }, [...job.events].reverse().map((e) => h('li', {}, h('time', {}, fmtTime(e.time)), e.message)))));

    return done;
  }

  // Polling đến khi job kết thúc (nâng lên SSE/SignalR khi cần, mục 8 của kế hoạch).
  if (!(await load())) {
    const t = setInterval(async () => { if (await load().catch(() => false)) clearInterval(t); }, 1500);
    timers.push(t);
  }
}

// ---------------------------------------------------------------- router

async function route() {
  clearTimers();
  every(10000, refreshWorkerPill);
  const hash = location.hash || '#/';
  const section = hash.startsWith('#/orders') || hash.startsWith('#/jobs') ? 'orders' : 'configurator';
  document.querySelectorAll('[data-nav]').forEach((a) => a.classList.toggle('active', a.dataset.nav === section));
  try {
    if (hash.startsWith('#/jobs/')) await renderJob(decodeURIComponent(hash.slice(7)));
    else if (hash.startsWith('#/orders')) await renderOrders();
    else await renderConfigurator(hash.startsWith('#/new/') ? decodeURIComponent(hash.slice(6)) : undefined);
  } catch (e) {
    view.replaceChildren(h('div', { class: 'card' }, h('h1', {}, 'Something went wrong'), h('p', { class: 'muted' }, e.message)));
  }
}

addEventListener('hashchange', route);
refreshWorkerPill();
route();
