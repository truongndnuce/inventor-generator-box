import { useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertDialog, Badge, Button, Card, Checkbox, Dialog, DropdownMenu, Flex, Heading, IconButton, Select, Switch, Text, TextArea, TextField,
} from '@radix-ui/themes';
import { api, fmtDate, go, postJson } from '../lib.js';
import { Notice, ParamField } from '../components.jsx';

const STATUS = {
  draft: { icon: '⚠', label: 'Draft' },
  active: { icon: '✓', label: 'Active' },
  deprecated: { icon: '✕', label: 'Deprecated' },
};

function StatusIcon({ status }) {
  const s = STATUS[status] || STATUS.draft;
  return <span className={`sicon sicon-${status}`} title={s.label}>{s.icon}</span>;
}

const fieldText = (f, v) => {
  if (v == null || v === '') return '-';
  if (f.type === 'multi') return v.split(',').join(' · ');
  return f.unit ? `${v} ${f.unit}` : v;
};

// ------------------------------------------------------------------ dialog to create / edit a Configuration

function ConfigDialog({ open, onClose, onSaved, code, schema, cfgSchema, initial, editingId }) {
  const [form, setForm] = useState(initial);
  const [issues, setIssues] = useState([]);
  const [bad, setBad] = useState(new Set());
  const [valid, setValid] = useState(true);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState(null);
  const seq = useRef(0);

  useEffect(() => { setForm(initial); setNote(null); }, [initial]);

  useEffect(() => {
    if (!open) return undefined;
    const mine = ++seq.current;
    const t = setTimeout(async () => {
      try {
        const r = await postJson(`/api/products/${code}/validate`, { values: form.values });
        if (mine !== seq.current) return;
        setValid(r.valid); setIssues(r.issues.map((i) => i.message)); setBad(new Set(r.issues.flatMap((i) => i.params)));
      } catch (e) { setNote('Could not validate: ' + e.message); }
    }, 250);
    return () => clearTimeout(t);
  }, [form.values, open, code]);

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));
  const setIn = (group, name, v) => setForm((f) => ({ ...f, [group]: { ...f[group], [name]: v } }));

  const save = async () => {
    setBusy(true); setNote(null);
    try {
      const body = { ...form, updatedBy: localStorage.getItem('cadflow.user') || '' };
      const path = `/api/products/${code}/configs${editingId ? `/${editingId}` : ''}`;
      const saved = await api(path, { method: editingId ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      onSaved(saved);
    } catch (e) { setNote(e.message); }
    setBusy(false);
  };

  const renderField = (group, f) => {
    const v = form[group][f.name] ?? '';
    return (
      <div className="field cfg-field" key={f.name}>
        <Text as="label" size="2" weight="medium">{f.label}</Text>
        {f.type === 'select' && (
          <Select.Root value={v} onValueChange={(x) => setIn(group, f.name, x)}>
            <Select.Trigger aria-label={f.label} />
            <Select.Content>{f.options.map((o) => <Select.Item key={o} value={o}>{o}</Select.Item>)}</Select.Content>
          </Select.Root>
        )}
        {f.type === 'number' && (
          <TextField.Root type="number" value={v} onChange={(e) => setIn(group, f.name, e.target.value)}>
            {f.unit && <TextField.Slot side="right">{f.unit}</TextField.Slot>}
          </TextField.Root>
        )}
        {f.type === 'multi' && (
          <Flex gap="4" wrap="wrap">
            {f.options.map((o) => {
              const on = v.split(',').includes(o);
              return (
                <Text as="label" size="2" key={o}>
                  <Flex gap="2" align="center">
                    <Checkbox checked={on} onCheckedChange={(c) => {
                      const cur = v.split(',').filter(Boolean);
                      setIn(group, f.name, f.options.filter((x) => (x === o ? c : cur.includes(x))).join(','));
                    }} />{o}
                  </Flex>
                </Text>
              );
            })}
          </Flex>
        )}
      </div>
    );
  };

  return (
    <Dialog.Root open={open} onOpenChange={(o) => !o && onClose()}>
      <Dialog.Content maxWidth="920px" style={{ maxHeight: '88vh' }}>
        <Dialog.Title>{editingId ? `Edit configuration (revision ${form.revision} → ${form.revision + 1})` : 'New configuration'}</Dialog.Title>
        <Flex direction="column" gap="3">
          <Notice color="amber">{note}</Notice>
          <Card variant="surface">
            <Heading size="3" mb="2">Configuration name</Heading>
            <div className="field cfg-field"><Text as="label" size="2" weight="medium">Name</Text>
              <TextField.Root value={form.name} placeholder="e.g. TR-110kV-1.0" onChange={(e) => set({ name: e.target.value })} /></div>
            <div className="field cfg-field"><Text as="label" size="2" weight="medium">Description</Text>
              <TextArea value={form.description} rows={2} onChange={(e) => set({ description: e.target.value })} /></div>
          </Card>
          <Card variant="surface">
            <Heading size="3" mb="2">Status</Heading>
            <div className="field cfg-field"><Text as="label" size="2" weight="medium">Status</Text>
              <Select.Root value={form.status} onValueChange={(s) => set({ status: s, isDefault: s === 'active' ? form.isDefault : false })}>
                <Select.Trigger aria-label="Status" />
                <Select.Content>{cfgSchema.statuses.map((s) => <Select.Item key={s} value={s}>{STATUS[s].label}</Select.Item>)}</Select.Content>
              </Select.Root></div>
            <div className="field cfg-field"><Text as="label" size="2" weight="medium">Default for new orders</Text>
              <Flex align="center" gap="2"><Switch checked={form.isDefault} disabled={form.status !== 'active'} onCheckedChange={(c) => set({ isDefault: c })} />
                <Text size="1" color="gray">{form.status === 'active' ? 'Used when an order names no configuration' : 'Only an active configuration can be the default'}</Text></Flex></div>
            <Text size="1" color="gray">Only active configurations can be chosen when ordering.</Text>
          </Card>
          {cfgSchema.groups.map((g) => (
            <Card variant="surface" key={g.key}>
              <Heading size="3" mb="2">{g.label}</Heading>
              {g.fields.map((f) => renderField(g.key, f))}
            </Card>
          ))}
          <Heading size="3" mt="2">Config components</Heading>
          {schema.groups.map((g) => (
            <Card variant="surface" key={g}>
              <Heading size="2" mb="2">{g}</Heading>
              {schema.parameters.filter((p) => p.group === g).map((p) => (
                <ParamField key={p.name} p={p} value={form.values[p.name]} invalid={bad.has(p.name)}
                  note={form.values[p.name] !== schema.factory[p.name] ? `Factory: ${schema.factory[p.name]}` : undefined}
                  onChange={(v) => setForm((f) => ({ ...f, values: { ...f.values, [p.name]: v } }))} />
              ))}
            </Card>
          ))}
          {issues.length > 0 && <Notice><ul style={{ margin: 0, paddingLeft: 18 }}>{issues.map((m, i) => <li key={i}>{m}</li>)}</ul></Notice>}
        </Flex>
        <Flex gap="3" mt="4" justify="end">
          <Button variant="soft" color="gray" onClick={onClose}>Cancel</Button>
          <Button disabled={busy || !valid || !form.name.trim()} onClick={save}>{editingId ? 'Save revision' : 'Create configuration'}</Button>
        </Flex>
      </Dialog.Content>
    </Dialog.Root>
  );
}

// ------------------------------------------------------------------ Config page: a matrix comparing the Configurations

export default function Config({ code }) {
  const [products, setProducts] = useState(null);
  const [schema, setSchema] = useState(null);
  const [cfgSchema, setCfgSchema] = useState(null);
  const [configs, setConfigs] = useState(null);
  const [note, setNote] = useState(null);
  const [filter, setFilter] = useState('all');
  const [search, setSearch] = useState('');
  const [closed, setClosed] = useState(new Set()); // the groups on the left that are collapsed
  const [dialog, setDialog] = useState(null);      // { initial, editingId }
  const [toDelete, setToDelete] = useState(null);

  const reload = async (productCode) => {
    setConfigs(await api(`/api/products/${productCode}/configs`));
  };

  useEffect(() => {
    let alive = true;
    setSchema(null); setConfigs(null); setNote(null);
    (async () => {
      try {
        const list = await api('/api/products');
        const product = list.find((p) => p.code === code) || list[0];
        const [s, cs, cf] = await Promise.all([api(`/api/products/${product.code}`), api('/api/config-schema'), api(`/api/products/${product.code}/configs`)]);
        if (!alive) return;
        setProducts(list); setSchema({ ...s, name: product.name }); setCfgSchema(cs); setConfigs(cf);
      } catch (e) { if (alive) setNote(e.message); }
    })();
    return () => { alive = false; };
  }, [code]);

  const shown = useMemo(() => (configs || []).filter((c) =>
    (filter === 'all' || c.status === filter) && c.name.toLowerCase().includes(search.trim().toLowerCase())), [configs, filter, search]);

  // The rows of the matrix. Each entry: a group (collapsible) holding value rows or a sub-group.
  const sections = useMemo(() => {
    if (!schema || !cfgSchema) return [];
    const fieldRows = (g) => g.fields.map((f) => ({
      label: f.unit ? `${f.label} (${f.unit})` : f.label,
      cell: (c) => fieldText({ ...f, unit: '' }, c[g.key]?.[f.name]),
    }));
    return [
      { key: 'name', label: 'Configuration Name', rows: [
        { label: 'Name', cell: (c) => c.name },
        { label: 'Description', cell: (c) => c.description || '-' },
        { label: 'Default for orders', cell: (c) => (c.isDefault ? 'Yes' : '-') },
      ] },
      { key: 'status', label: 'Status', rows: [
        { label: 'Status', cell: (c) => <Badge color={{ active: 'green', draft: 'amber', deprecated: 'red' }[c.status]} variant="soft">{STATUS[c.status].label}</Badge> },
        { label: 'Revision', cell: (c) => c.revision },
        { label: 'Created', cell: (c) => fmtDate(c.createdAt) },
        { label: 'Updated', cell: (c) => fmtDate(c.updatedAt) },
        { label: 'Updated by', cell: (c) => c.updatedBy },
      ] },
      ...cfgSchema.groups.map((g) => ({ key: g.key, label: g.label, rows: fieldRows(g) })),
      { key: 'components', label: 'Config Components', subgroups: schema.groups.map((gr) => ({
        key: `c:${gr}`, label: gr,
        rows: schema.parameters.filter((p) => p.group === gr).map((p) => ({
          label: p.unit ? `${p.label} (${p.unit})` : p.label,
          cell: (c) => c.values[p.name],
          differs: (c) => c.values[p.name] !== schema.factory[p.name],
        })),
      })) },
    ];
  }, [schema, cfgSchema]);

  if (!schema || !cfgSchema || !configs) return <Text color="gray">{note || 'Loading configurations...'}</Text>;

  const toggle = (k) => setClosed((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n; });
  const blank = () => ({
    name: '', description: '', status: 'draft', isDefault: false, revision: 0,
    operation: Object.fromEntries(cfgSchema.groups[0].fields.map((f) => [f.name, f.default])),
    cad: Object.fromEntries(cfgSchema.groups[1].fields.map((f) => [f.name, f.default])),
    values: { ...schema.factory },
  });
  const fromConfig = (c, copy) => ({ ...c, name: copy ? `${c.name}-copy` : c.name, status: copy ? 'draft' : c.status, isDefault: copy ? false : c.isDefault });

  const action = async (fn) => {
    setNote(null);
    try { await fn(); await reload(schema.code); } catch (e) { setNote(e.message); }
  };
  const setDefault = (c) => action(() => api(`/api/products/${schema.code}/configs/${c.id}`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ isDefault: true, updatedBy: localStorage.getItem('cadflow.user') || '' }),
  }));

  const cols = `250px repeat(${Math.max(shown.length, 1)}, minmax(230px, 1fr))`;
  const bodyRows = [];
  const pushRows = (rows, prefix, indent) => rows.forEach((r, i) => bodyRows.push(
    <div className="cfg-row" key={`${prefix}${i}`} style={{ display: 'contents' }}>
      <div className={`cfg-label l${indent}`} title={r.label}>{r.label}</div>
      {shown.map((c) => <div className={`cfg-cell${r.differs?.(c) ? ' differs' : ''}`} key={c.id}>{r.cell(c)}</div>)}
      {shown.length === 0 && <div className="cfg-cell" />}
    </div>,
  ));
  const groupHead = (k, label, level) => (
    <div style={{ display: 'contents' }} key={`h${k}`}>
      <button type="button" className={`cfg-label gh gh${level}`} onClick={() => toggle(k)} aria-expanded={!closed.has(k)}>
        <span className="chev">{closed.has(k) ? '›' : '⌄'}</span>{label}
      </button>
      {shown.map((c) => <div className={`cfg-cell gh gh${level}`} key={c.id} />)}
      {shown.length === 0 && <div className="cfg-cell gh" />}
    </div>
  );
  sections.forEach((s) => {
    bodyRows.push(groupHead(s.key, s.label, 1));
    if (closed.has(s.key)) return;
    if (s.rows) pushRows(s.rows, s.key, 1);
    s.subgroups?.forEach((sg) => {
      bodyRows.push(groupHead(sg.key, sg.label, 2));
      if (!closed.has(sg.key)) pushRows(sg.rows, sg.key, 2);
    });
  });

  return (
    <Flex direction="column" gap="3">
      <Flex align="center" gap="3" wrap="wrap">
        <Heading size="5">Configurations</Heading>
        <Select.Root value={schema.code} onValueChange={(v) => go(`#/config/${v}`)}>
          <Select.Trigger aria-label="Product" style={{ minWidth: 200 }} />
          <Select.Content>{products.map((p) => <Select.Item key={p.code} value={p.code}>{p.name}</Select.Item>)}</Select.Content>
        </Select.Root>
        <TextField.Root placeholder="Search configuration" value={search} onChange={(e) => setSearch(e.target.value)} style={{ minWidth: 200 }} />
        <Flex align="center" gap="2">
          <Text size="2" color="gray">Configuration status</Text>
          <Select.Root value={filter} onValueChange={setFilter}>
            <Select.Trigger aria-label="Status filter" variant="soft" radius="full" />
            <Select.Content>
              <Select.Item value="all">All</Select.Item>
              {cfgSchema.statuses.map((s) => <Select.Item key={s} value={s}>{STATUS[s].label}</Select.Item>)}
            </Select.Content>
          </Select.Root>
        </Flex>
        <Flex ml="auto"><Button onClick={() => setDialog({ initial: blank(), editingId: null })}>+ New configuration</Button></Flex>
      </Flex>

      <Notice color="amber">{note}</Notice>
      <Text size="1" color="gray">
        {shown.length} of {configs.length} configuration(s). Teal values differ from the factory defaults. An order picks one active configuration
        (or the default) and takes the component values from it.
      </Text>

      <Card size="1" className="cfg-card">
        <div className="cfg-scroll">
          <div className="cfg-grid" style={{ gridTemplateColumns: cols }}>
            <div className="cfg-corner">Parameter Groups</div>
            {shown.map((c) => (
              <div className="cfg-head" key={c.id}>
                <a href="#/config" className="cfg-name" onClick={(e) => { e.preventDefault(); setDialog({ initial: fromConfig(c), editingId: c.id }); }} title="Edit this configuration">{c.name}</a>
                {c.isDefault && <Badge size="1" color="teal" variant="soft">default</Badge>}
                <StatusIcon status={c.status} />
                <DropdownMenu.Root>
                  <DropdownMenu.Trigger><IconButton variant="ghost" color="gray" size="1" aria-label={`Actions for ${c.name}`}>⋮</IconButton></DropdownMenu.Trigger>
                  <DropdownMenu.Content>
                    <DropdownMenu.Item onSelect={() => setDialog({ initial: fromConfig(c), editingId: c.id })}>Edit</DropdownMenu.Item>
                    <DropdownMenu.Item onSelect={() => setDialog({ initial: fromConfig(c, true), editingId: null })}>Duplicate</DropdownMenu.Item>
                    <DropdownMenu.Item disabled={c.status !== 'active' || c.isDefault} onSelect={() => setDefault(c)}>Set as default</DropdownMenu.Item>
                    <DropdownMenu.Item onSelect={() => go(`#/new/${schema.code}?config=${c.id}`)} disabled={c.status !== 'active'}>Order with this</DropdownMenu.Item>
                    <DropdownMenu.Separator />
                    <DropdownMenu.Item color="red" onSelect={() => setToDelete(c)}>Delete</DropdownMenu.Item>
                  </DropdownMenu.Content>
                </DropdownMenu.Root>
              </div>
            ))}
            {shown.length === 0 && <div className="cfg-head"><Text size="2" color="gray">No configuration matches.</Text></div>}
            {bodyRows}
          </div>
        </div>
      </Card>

      {dialog && (
        <ConfigDialog open code={schema.code} schema={schema} cfgSchema={cfgSchema} initial={dialog.initial} editingId={dialog.editingId}
          onClose={() => setDialog(null)} onSaved={async () => { setDialog(null); await reload(schema.code); }} />
      )}

      <AlertDialog.Root open={!!toDelete} onOpenChange={(o) => !o && setToDelete(null)}>
        <AlertDialog.Content maxWidth="440px">
          <AlertDialog.Title>Delete configuration</AlertDialog.Title>
          <AlertDialog.Description size="2">
            Delete “{toDelete?.name}”? Finished jobs keep their own values, but new orders can no longer pick it.
          </AlertDialog.Description>
          <Flex gap="3" mt="4" justify="end">
            <AlertDialog.Cancel><Button variant="soft" color="gray">Cancel</Button></AlertDialog.Cancel>
            <AlertDialog.Action>
              <Button color="red" onClick={() => { const c = toDelete; setToDelete(null); action(() => api(`/api/products/${schema.code}/configs/${c.id}`, { method: 'DELETE' })); }}>Delete</Button>
            </AlertDialog.Action>
          </Flex>
        </AlertDialog.Content>
      </AlertDialog.Root>
    </Flex>
  );
}
