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

function setNav(section) {
  document.querySelectorAll('[data-nav]').forEach((a) => a.classList.toggle('active', a.dataset.nav === section));
}

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
      h('div', { class: 'row' }, who, submit, importGeaControl(note))));
  validate();
}

// ---------------------------------------------------------------- Import .gea / đặt hàng từ script

/** Nút "Import .gea": gửi file lên server để biên dịch; thành công thì chuyển sang trang đặt hàng từ script. */
function importGeaControl(note) {
  const input = h('input', { type: 'file', accept: '.gea,text/plain', hidden: true });
  input.addEventListener('change', async () => {
    const file = input.files[0];
    input.value = '';
    if (!file) return;
    note.hidden = true;
    try {
      const text = await file.text();
      const res = await postJson('/api/gea/import', { text });
      sessionStorage.setItem('cadflow.import', JSON.stringify({ scriptId: res.scriptId, values: res.values, texts: res.texts, warnings: res.warnings, hasValues: res.hasValues, fileName: file.name }));
      location.hash = `#/import/${res.scriptId}`;
    } catch (e) {
      note.hidden = false;
      const diags = (e.body?.diagnostics || []).filter((d) => d.severity === 'error').slice(0, 5);
      note.replaceChildren(h('div', {}, `Could not import ${file.name}: ${e.message}`),
        ...diags.map((d) => h('div', { class: 'muted' }, `line ${d.line}, col ${d.column}: ${d.code} ${d.message}`)));
    }
  });
  return h('span', { class: 'import' }, input,
    h('button', { type: 'button', title: 'Order from a .gea script (for example one exported from CADFlow)', onclick: () => input.click() }, 'Import .gea'));
}

/** Mở lại một job đã có trên form New order (job dựng từ script nhập vào thì mở lại form của script đó). */
function reorder(job) {
  if (job.productCode === 'imported' && job.scriptSha256) {
    sessionStorage.setItem('cadflow.importRetry', JSON.stringify({ scriptId: job.scriptSha256, values: job.inputs, texts: job.textInputs }));
    location.hash = `#/import/${job.scriptSha256}`;
  } else {
    sessionStorage.setItem('cadflow.retry', JSON.stringify(job.inputs));
    location.hash = `#/new/${job.productCode}`;
  }
}

/** Form động từ khối FORM của một script .gea đã import: ô ẩn/hiện và luật ASSERT do server tính lại mỗi khi giá trị đổi. */
async function renderScriptOrder(scriptId) {
  view.replaceChildren(h('p', { class: 'muted' }, 'Loading script...'));
  let imported = {}, retry = {};
  try { const s = JSON.parse(sessionStorage.getItem('cadflow.import') || '{}'); if (s.scriptId === scriptId) imported = s; } catch { /* bỏ qua */ }
  try { const s = JSON.parse(sessionStorage.getItem('cadflow.importRetry') || '{}'); if (s.scriptId === scriptId) retry = s; } catch { /* bỏ qua */ }
  sessionStorage.removeItem('cadflow.importRetry');

  const values = { ...(imported.values || {}), ...(retry.values || {}) };   // ô số và ô tích (1/0)
  const texts = { ...(imported.texts || {}), ...(retry.texts || {}) };      // ô text và ô chọn một
  let result = await postJson(`/api/gea/${scriptId}/form`, { values, texts });
  const isText = (f) => f.kind === 'radio' || f.kind === 'text';
  for (const f of result.form.fields) {
    if (isText(f)) { if (!(f.name in texts)) texts[f.name] = f.value; }
    else if (!(f.name in values)) values[f.name] = f.value;
  }

  const rows = new Map();   // tên ô -> { field, input }
  const groups = [...new Set(result.form.fields.map((f) => f.group || ''))];
  const issuesBox = h('ul', { class: 'issues', hidden: true });
  const warnBox = h('ul', { class: 'notice', hidden: true });
  const notesBox = h('div', { class: 'muted' });
  const note = h('div', { class: 'notice', hidden: true });
  const submit = h('button', { class: 'primary', type: 'button' }, 'Submit order');
  const who = h('input', { type: 'text', placeholder: 'Requested by', value: localStorage.getItem('cadflow.user') || '' });

  let seq = 0, timer;
  const schedule = () => { submit.disabled = true; clearTimeout(timer); timer = setTimeout(evaluate, 250); };

  const form = h('div', {}, groups.map((g) => h('fieldset', {}, g && h('legend', {}, g),
    result.form.fields.filter((f) => (f.group || '') === g).map((f) => {
      const label = h('label', { for: f.name, title: f.tooltip || '' }, f.label);
      let controls;
      if (f.kind === 'radio') {
        const select = h('select', { id: f.name }, f.options.map((o) => h('option', { value: o, selected: o === texts[f.name] }, o)));
        select.addEventListener('change', () => { texts[f.name] = select.value; schedule(); });
        controls = [select];
        rows.set(f.name, { field: null, input: select });
      } else if (f.kind === 'text') {
        const box = h('input', { type: 'text', id: f.name, value: texts[f.name] });
        box.addEventListener('input', () => { texts[f.name] = box.value; schedule(); });
        controls = [box];
        rows.set(f.name, { field: null, input: box });
      } else if (f.kind === 'checkbox') {
        const box = h('input', { type: 'checkbox', id: f.name, checked: values[f.name] === 1 });
        box.addEventListener('change', () => { values[f.name] = box.checked ? 1 : 0; schedule(); });
        controls = [box];
        rows.set(f.name, { field: null, input: box });
      } else {
        const number = h('input', { type: 'number', id: f.name, min: f.min, max: f.max, step: f.integer ? 1 : (f.step || 'any'), value: values[f.name] });
        number.addEventListener('input', () => { if (number.value !== '') { values[f.name] = Number(number.value); schedule(); } });
        controls = [number, h('span', { class: 'range' }, f.min != null && f.max != null ? `${f.min}-${f.max} ${f.unit}`.trim() : f.unit)];
        rows.set(f.name, { field: null, input: number });
      }
      const field = h('div', { class: 'field script-field' }, label, controls);
      rows.get(f.name).field = field;
      return field;
    }))));

  function show(r) {
    result = r;
    const bad = new Set(r.form.errors.flatMap((e) => e.params));
    for (const f of r.form.fields) {
      const row = rows.get(f.name);
      row.field.hidden = !f.visible;
      row.field.classList.toggle('invalid', f.errors.length > 0 || bad.has(f.name));
    }
    issuesBox.hidden = r.form.errors.length === 0;
    issuesBox.replaceChildren(...r.form.errors.map((e) => h('li', {}, e.message)));
    const warns = [...(imported.warnings || []), ...r.form.warnings.map((w) => w.message)];
    warnBox.hidden = warns.length === 0;
    warnBox.replaceChildren(...warns.map((w) => h('li', {}, w)));
    notesBox.replaceChildren(...r.form.notes.map((n) => h('div', {}, n)));
    submit.disabled = !r.form.ok;
  }
  async function evaluate() {
    const mine = ++seq;
    try {
      const r = await postJson(`/api/gea/${scriptId}/form`, { values, texts });
      if (mine === seq) show(r);
    } catch (e) {
      note.hidden = false;
      note.textContent = 'Could not evaluate the form: ' + e.message;
    }
  }

  submit.addEventListener('click', async () => {
    submit.disabled = true;
    try {
      localStorage.setItem('cadflow.user', who.value);
      const job = await postJson('/api/jobs', { scriptId, inputs: values, textInputs: texts, requestedBy: who.value });
      location.hash = `#/jobs/${job.id}`;
    } catch (e) {
      note.hidden = false;
      note.textContent = 'Order rejected: ' + (e.body?.issues?.[0]?.message || e.message);
      submit.disabled = false;
    }
  });

  view.replaceChildren(h('div', { class: 'card' },
    h('div', { class: 'tabs' }, h('a', { href: '#/', class: 'tab' }, 'Products'), h('span', { class: 'tab active' }, 'Imported script')),
    h('h1', {}, result.name || imported.fileName || 'Imported script'),
    h('p', { class: 'muted' }, 'This order runs the imported .gea script on the Inventor worker. The form below comes from the script\'s own FORM block; the server checks its rules again when you submit.'),
    note, warnBox, form, notesBox, issuesBox,
    h('div', { class: 'row' }, who, submit)));
  show(result);
}

// ---------------------------------------------------------------- Ordered / Dashboard

/** Tên sản phẩm hiển thị: job đặt từ script import ghi rõ tên script. */
const productLabel = (j) => (j.productCode === 'imported' ? `${j.scriptName || 'script'} (imported)` : j.productCode);

const JOB_COLUMNS =['Job', 'Product', 'Status', 'Created', 'Requested by', 'Worker'];

function jobRow(j) {
  return h('tr', {},
    h('td', {}, h('a', { href: `#/jobs/${j.id}` }, j.id)), h('td', {}, productLabel(j)),
    h('td', {}, statusBadge(j.status), j.step && j.status === 'running' ? ` ${j.step}` : ''),
    h('td', {}, fmtDate(j.createdAt)), h('td', {}, j.requestedBy), h('td', {}, j.worker || '-'));
}

/** Ordered: chỉ job đang chờ/đang chạy. Bấm vào chỉ thấy tiến độ; job xong tự rời danh sách (xem ở Dashboard). */
async function renderOrders() {
  const body = h('tbody');
  view.replaceChildren(h('div', { class: 'card' }, h('h1', {}, 'Ordered'),
    h('p', { class: 'muted' }, 'Jobs that are waiting or being built. Finished jobs and their drawings are in the ',
      h('a', { href: '#/dashboard' }, 'Dashboard'), '.'),
    h('table', {}, h('thead', {}, h('tr', {}, JOB_COLUMNS.map((t) => h('th', {}, t)))), body)));

  async function load() {
    const jobs = await api('/api/jobs?status=queued,running');
    body.replaceChildren(...(jobs.length
      ? jobs.map(jobRow)
      : [h('tr', {}, h('td', { colspan: JOB_COLUMNS.length, class: 'muted' }, 'No jobs are running right now.'))]));
  }
  await load();
  every(3000, () => load().catch(() => {}));
}

const SVG_NS = 'http://www.w3.org/2000/svg';
function svgEl(tag, attrs = {}, ...children) {
  const el = document.createElementNS(SVG_NS, tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null && v !== false) el.setAttribute(k, v);
  for (const c of children) if (c != null) el.append(c);
  return el;
}

const fmtDuration = (s) => (s == null ? '-' : s >= 90 ? `${Math.floor(s / 60)} min ${Math.round(s % 60)} s` : `${Math.round(s)} s`);

/** Biểu đồ cột một chuỗi: points = [{ label, value, tip }]. Cột bo góc ở đầu, hover hiện tooltip. */
function barChart(title, points, { integer = false, unit = '' } = {}) {
  const W = 460, H = 170, L = 36, R = 8, T = 10, B = 24;
  const max = Math.max(...points.map((p) => p.value ?? 0), 0);
  const top = max === 0 ? 1 : integer ? Math.max(1, Math.ceil(max)) : Math.ceil(max / 10) * 10 || 10;
  const slot = (W - L - R) / points.length;
  const bw = Math.max(3, Math.min(28, slot * 0.6));
  const y = (v) => T + (H - T - B) * (1 - v / top);
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, class: 'chart', role: 'img', 'aria-label': title });
  const tipBox = h('div', { class: 'tooltip', hidden: true });

  for (const v of [0, top]) {
    svg.append(svgEl('line', { x1: L, x2: W - R, y1: y(v), y2: y(v), class: v === 0 ? 'axis' : 'grid' }));
    const t = svgEl('text', { x: L - 6, y: y(v) + 4, 'text-anchor': 'end', class: 'tick' });
    t.textContent = String(v);
    svg.append(t);
  }
  const labelEvery = Math.ceil(points.length / 5);
  points.forEach((p, i) => {
    const x = L + slot * i + (slot - bw) / 2;
    const v = p.value ?? 0;
    const bar = v > 0 ? (() => {
      const y0 = y(0), y1 = y(v), r = Math.min(3, (y0 - y1) / 2, bw / 2);
      return svgEl('path', { d: `M${x},${y0} V${y1 + r} Q${x},${y1} ${x + r},${y1} H${x + bw - r} Q${x + bw},${y1} ${x + bw},${y1 + r} V${y0} Z`, class: 'bar' });
    })() : null;
    if (bar) svg.append(bar);
    if ((points.length - 1 - i) % labelEvery === 0) { // đếm từ ngày cuối để luôn có nhãn cuối và không đè nhau
      const t = svgEl('text', { x: x + bw / 2, y: H - 6, 'text-anchor': 'middle', class: 'tick' });
      t.textContent = p.label;
      svg.append(t);
    }
    // Vùng bắt hover rộng hơn cột (cả khe và cột rỗng).
    const hit = svgEl('rect', { x: L + slot * i, y: T, width: slot, height: H - T - B, class: 'hit' });
    hit.addEventListener('mouseenter', () => { bar?.classList.add('hot'); tipBox.hidden = false; tipBox.textContent = p.tip; });
    hit.addEventListener('mousemove', (e) => {
      const r = tipBox.parentElement.getBoundingClientRect();
      tipBox.style.left = `${Math.min(e.clientX - r.left + 12, r.width - tipBox.offsetWidth - 4)}px`;
      tipBox.style.top = `${Math.max(e.clientY - r.top - 34, 0)}px`;
    });
    hit.addEventListener('mouseleave', () => { bar?.classList.remove('hot'); tipBox.hidden = true; });
    svg.append(hit);
  });
  const empty = max === 0 ? h('div', { class: 'chart-empty muted' }, 'No data in this period') : null;
  return h('div', { class: 'chart-card' }, h('h2', {}, title, unit && h('span', { class: 'muted' }, ` (${unit})`)), h('div', { class: 'chart-wrap' }, svg, tipBox, empty));
}

const STAT_ORDER = ['queued', 'running', 'succeeded', 'failed', 'cancelled'];

/** Dashboard: thẻ thống kê, biểu đồ theo ngày, và toàn bộ job đã chạy (có lọc). Job 'succeeded' mở ra được trang kết quả. */
async function renderDashboard() {
  const STATUSES = ['', ...STAT_ORDER];
  const status = h('select', {}, STATUSES.map((s) => h('option', { value: s }, s || 'All statuses')));
  const product = h('select', {}, h('option', { value: '' }, 'All products'), h('option', { value: 'imported' }, 'Imported scripts'));
  const days = h('select', {}, [[7, 'Last 7 days'], [14, 'Last 14 days'], [30, 'Last 30 days'], [90, 'Last 90 days']].map(([v, t]) => h('option', { value: v, selected: v === 14 }, t)));
  const mine = h('input', { type: 'checkbox', id: 'only-mine' });
  const user = localStorage.getItem('cadflow.user') || '';
  const mineLabel = h('label', { for: 'only-mine' }, user ? ` Only mine (${user})` : ' Only mine (set "Requested by" in New order first)');
  mine.disabled = !user;
  const body = h('tbody');
  const count = h('span', { class: 'muted' });
  const tiles = h('div', { class: 'tiles' });
  const charts = h('div', { class: 'charts' });
  const COLUMNS = [...JOB_COLUMNS, ''];

  view.replaceChildren(h('div', { class: 'card' }, h('h1', {}, 'Dashboard'),
    h('div', { class: 'row' }, days, product, status, h('span', {}, mine, mineLabel), count)),
    tiles, charts,
    h('div', { class: 'card' }, h('h2', {}, 'Jobs'),
      h('table', {}, h('thead', {}, h('tr', {}, COLUMNS.map((t) => h('th', {}, t)))), body)));

  try {
    for (const p of await api('/api/products')) product.append(h('option', { value: p.code }, p.name));
  } catch { /* lọc theo sản phẩm là phụ; bỏ qua nếu lỗi */ }

  const tile = (label, value, sub, cls) => h('div', { class: `tile ${cls || ''}` }, h('div', { class: 'tile-label' }, label), h('div', { class: 'tile-value' }, value), sub && h('div', { class: 'tile-sub muted' }, sub));

  async function loadStats() {
    const q = new URLSearchParams({ days: days.value });
    if (product.value) q.set('product', product.value);
    if (mine.checked && user) q.set('requestedBy', user);
    const s = await api(`/api/stats?${q}`);
    tiles.replaceChildren(
      tile('Jobs', String(s.total), `last ${s.days} days`),
      tile('Succeeded', String(s.byStatus.succeeded), null, 's-ok'),
      tile('Failed', String(s.byStatus.failed), `${s.byStatus.cancelled} cancelled`, s.byStatus.failed ? 's-bad' : ''),
      tile('Success rate', s.successRate == null ? '-' : `${s.successRate}%`, 'succeeded / (succeeded + failed)'),
      tile('Avg build time', fmtDuration(s.avgBuildSeconds), 'succeeded jobs'));
    const shortDate = (d) => d.slice(5);
    charts.replaceChildren(
      barChart('Jobs per day', s.byDay.map((d) => ({ label: shortDate(d.date), value: d.total, tip: `${d.date}: ${d.total} job${d.total === 1 ? '' : 's'} (${d.succeeded} succeeded, ${d.failed} failed)` })), { integer: true }),
      barChart('Average build time', s.byDay.map((d) => ({ label: shortDate(d.date), value: d.avgBuildSeconds, tip: `${d.date}: ${d.avgBuildSeconds == null ? 'no finished builds' : fmtDuration(d.avgBuildSeconds)}` })), { unit: 'seconds' }));
  }

  async function loadJobs() {
    const q = new URLSearchParams();
    if (status.value) q.set('status', status.value);
    if (product.value) q.set('product', product.value);
    if (mine.checked && user) q.set('requestedBy', user);
    const jobs = await api('/api/jobs' + (q.toString() ? `?${q}` : ''));
    count.textContent = `${jobs.length} job${jobs.length === 1 ? '' : 's'} listed`;
    body.replaceChildren(...(jobs.length
      ? jobs.map((j) => { const row = jobRow(j); row.append(h('td', { class: 'actions' }, orderAgainButton(j), exportGeaLink(j))); return row; })
      : [h('tr', {}, h('td', { colspan: COLUMNS.length, class: 'muted' }, 'No jobs match.'))]));
  }

  const loadAll = () => Promise.all([loadStats(), loadJobs()]);
  [days, status, product, mine].forEach((el) => el.addEventListener('change', () => loadAll().catch(() => {})));
  await loadAll();
  every(5000, () => loadAll().catch(() => {}));
}

/** Liên kết tải file .gea (script đã dùng + giá trị) của job; null nếu job không dựng bằng script. */
function exportGeaLink(job) {
  if (!job.scriptSha256) return null;
  return h('a', { class: 'btn small', href: `${API}/api/jobs/${encodeURIComponent(job.id)}/export.gea`, download: `${job.id}.gea`, title: 'Download the .gea script with this job\'s values' }, 'Export .gea');
}

/** "Order again": đổ đầu vào của job cũ vào form New order (dùng chung cơ chế với "Edit and resubmit"). */
function orderAgainButton(job) {
  return h('button', { type: 'button', class: 'small', title: 'Open New order with this job\'s inputs', onclick: () => reorder(job) }, 'Order again');
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
    const dwg = job.artifacts.find((a) => a.kind === 'dwg');
    const url = (a) => `${API}/api/jobs/${encodeURIComponent(job.id)}/artifacts/${encodeURIComponent(a.name)}`;
    // Tải về: ?download=true để server ký URL R2 với Content-Disposition: attachment (thuộc tính download bị bỏ qua khi redirect sang origin khác).
    const dl = (a) => `${url(a)}?download=true`;

    // Job đang chờ/chạy thuộc "Ordered"; job đã xong thuộc "Dashboard".
    setNav(done ? 'dashboard' : 'orders');

    root.replaceChildren(
      h('div', { class: 'card' },
        h('p', { class: 'muted' }, h('a', { href: done ? '#/dashboard' : '#/orders' }, done ? '< Back to Dashboard' : '< Back to Ordered')),
        h('div', { class: 'row' }, h('h1', {}, job.id), statusBadge(job.status),
          !done && h('button', { onclick: async () => { await api(`/api/jobs/${job.id}/cancel`, { method: 'POST' }).catch(() => {}); load(); } }, 'Cancel'),
          job.status === 'failed' && h('button', { onclick: () => reorder(job) }, 'Edit and resubmit'),
          (job.status === 'succeeded' || job.status === 'cancelled') && orderAgainButton(job),
          exportGeaLink(job)),
        h('p', {}, message),
        h('div', { class: 'cols' },
          h('dl', { class: 'meta' },
            h('dt', {}, 'Product'), h('dd', {}, job.productCode === 'imported' ? productLabel(job) : `${job.productCode} v${job.productVersion}`),
            h('dt', {}, 'Requested by'), h('dd', {}, job.requestedBy),
            h('dt', {}, 'Created'), h('dd', {}, fmtDate(job.createdAt)),
            h('dt', {}, 'Started'), h('dd', {}, fmtDate(job.startedAt)),
            h('dt', {}, 'Finished'), h('dd', {}, fmtDate(job.finishedAt)),
            h('dt', {}, 'Worker'), h('dd', {}, job.worker || '-'),
            h('dt', {}, 'Attempts'), h('dd', {}, job.attempts),
            Object.keys(job.summary).length ? [h('dt', {}, 'Summary'), h('dd', {},
              Object.entries(job.summary).map(([k, v]) => `${k}: ${v}`).join('   '))] : null),
          h('dl', { class: 'meta' }, [...Object.entries(job.inputs), ...Object.entries(job.textInputs || {})].flatMap(([k, v]) => [h('dt', {}, k), h('dd', {}, job.productCode === 'imported' || /Count$/.test(k) ? String(v) : `${v} mm`)])))),
      job.artifactsPurgedAt && h('div', { class: 'card' },
        h('h2', {}, 'Result'),
        h('p', { class: 'muted' }, `The result files were deleted on ${fmtDate(job.artifactsPurgedAt)} (retention policy). Use Order again to rebuild them.`)),
      job.status === 'succeeded' && !job.artifactsPurgedAt && h('div', { class: 'card' },
        h('h2', {}, 'Result'),
        gallery(job.artifacts.filter((a) => a.kind === 'image'), url),
        h('div', { class: 'downloads' }, job.artifacts.map((a) => h('a', { href: dl(a), download: a.name }, `${a.name} (${fmtSize(a.size)})`)))),
      job.status === 'succeeded' && pdf && h('div', { class: 'card' },
        h('h2', {}, '2D drawing'),
        h('p', { class: 'muted' }, 'ISO 128/129 layout: first-angle projection, section A-A, overall and section dimensions.'),
        h('div', { class: 'downloads' },
          h('a', { href: dl(pdf), download: pdf.name }, `Download PDF (${fmtSize(pdf.size)})`),
          dwg && h('a', { href: dl(dwg), download: dwg.name }, `Download DWG (${fmtSize(dwg.size)})`)),
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
  // Trang job tự đặt mục nav theo trạng thái job (setNav trong renderJob).
  setNav(hash.startsWith('#/orders') ? 'orders' : hash.startsWith('#/dashboard') ? 'dashboard' : hash.startsWith('#/jobs') ? null : 'configurator');
  try {
    if (hash.startsWith('#/jobs/')) await renderJob(decodeURIComponent(hash.slice(7)));
    else if (hash.startsWith('#/import/')) await renderScriptOrder(decodeURIComponent(hash.slice(9)));
    else if (hash.startsWith('#/orders')) await renderOrders();
    else if (hash.startsWith('#/dashboard')) await renderDashboard();
    else await renderConfigurator(hash.startsWith('#/new/') ? decodeURIComponent(hash.slice(6)) : undefined);
  } catch (e) {
    view.replaceChildren(h('div', { class: 'card' }, h('h1', {}, 'Something went wrong'), h('p', { class: 'muted' }, e.message)));
  }
}

addEventListener('hashchange', route);
refreshWorkerPill();
route();
