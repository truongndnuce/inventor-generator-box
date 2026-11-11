import { useEffect, useRef, useState } from 'react';
import { Badge, Box, Button, Card, Checkbox, Flex, Heading, Popover, Select, Tabs, Text, TextArea, TextField } from '@radix-ui/themes';
import { downloadText, importScriptText, postJson, safeFileName } from '../lib.js';
import { Notice, ScriptField } from '../components.jsx';

const DRAFT_KEY = 'cadflow.designer';
const TYPES = ['LENGTH', 'ANGLE', 'INTEGER', 'DOUBLE', 'STRING', 'CHECKBOX', 'RADIO'];
const VAR_TYPES = ['LENGTH', 'ANGLE', 'INTEGER', 'DOUBLE'];
const UNITS = { LENGTH: ['mm', 'cm', 'm', 'in'], ANGLE: ['deg', 'rad'] };
const NUMERIC = ['LENGTH', 'ANGLE', 'INTEGER', 'DOUBLE'];

const BOX_BUILD = `  DECLARE_VARIABLE LENGTH innerRadius
  DECLARE_REFERENCE body
  DECLARE_REFERENCE lid

  innerRadius = MAX(BX_CornerRadius - BX_Wall, 0 mm)

  PROCESSING_BOX_START "Building box"
  CREATE_FEATURE builder:Tank
    FEATURE_IN "Length"       BX_Length
    FEATURE_IN "Width"        BX_Width
    FEATURE_IN "Height"       BX_Height
    FEATURE_IN "Wall"         BX_Wall
    FEATURE_IN "Bottom"       BX_Bottom
    FEATURE_IN "CornerRadius" BX_CornerRadius
    FEATURE_IN "InnerRadius"  innerRadius
    FEATURE_OUT body "Body"
  END_CREATE_FEATURE
  SET_REF_NAME body "BX_Box*"

  CREATE_FEATURE builder:Cover
    FEATURE_IN "Tank"      body
    FEATURE_IN "Thickness" BX_LidThickness
    FEATURE_IN "Lip"       BX_LidLip
    FEATURE_IN "Clearance" BX_LidClearance
    FEATURE_OUT lid "Body"
  END_CREATE_FEATURE
  SET_REF_NAME lid "BX_Lid*"

  REGEN_MDL
  PROCESSING_BOX_END`;

const emptyModel = () => ({
  version: 1,
  product: { name: '', prefix: '', target: 'PART' },
  groups: [{ title: 'General', params: [] }],
  rules: [], vars: [], steps: [],
});

const clone = (x) => JSON.parse(JSON.stringify(x));
const move = (arr, i, d) => { const j = i + d; if (j < 0 || j >= arr.length) return; [arr[i], arr[j]] = [arr[j], arr[i]]; };
const num = (v) => (v === '' ? undefined : Number(v));

/** What a type needs: numeric types have min/max (LENGTH and ANGLE also a unit); RADIO has options. Fields that do not apply are removed. */
function retype(p, type) {
  p.type = type;
  if (!NUMERIC.includes(type)) { delete p.min; delete p.max; delete p.step; }
  if (UNITS[type]) { if (!UNITS[type].includes(p.unit)) p.unit = UNITS[type][0]; } else delete p.unit;
  if (type === 'RADIO') { p.options = p.options?.length ? p.options : ['Option 1', 'Option 2']; p.default = p.options.includes(p.default) ? p.default : p.options[0]; } else delete p.options;
  if (type === 'CHECKBOX') p.default = p.default === 'FALSE' ? 'FALSE' : 'TRUE';
  else if (type === 'STRING') { if (p.default === 'TRUE' || p.default === 'FALSE') p.default = ''; }
  else if (NUMERIC.includes(type)) { if (p.default == null || Number.isNaN(Number(p.default)) || p.default === '') p.default = '0'; }
  if (type === 'INTEGER' && p.default && !Number.isInteger(Number(p.default))) p.default = String(Math.round(Number(p.default)));
}

/** A paragraph of small coloured messages. */
function Msgs({ items }) {
  if (!items.length) return null;
  return (
    <Box>
      {items.map((m, i) => (
        <Text as="div" size="1" key={i} color={m.warn ? 'amber' : 'red'}>
          {m.line ? `line ${m.line}: ` : ''}{m.text}
        </Text>
      ))}
    </Box>
  );
}

function Inp({ value, onChange, w, ...rest }) {
  return <TextField.Root size="1" value={value ?? ''} onChange={(e) => onChange(e.target.value)} style={{ width: w, minWidth: w }} {...rest} />;
}

function Pick({ value, options, onChange, w }) {
  return (
    <Select.Root size="1" value={value} onValueChange={onChange}>
      <Select.Trigger style={{ width: w }} />
      <Select.Content>{options.map((o) => <Select.Item key={o} value={o}>{o}</Select.Item>)}</Select.Content>
    </Select.Root>
  );
}

function Labeled({ label, children }) {
  return <Flex direction="column" gap="1"><Text size="1" color="gray">{label}</Text>{children}</Flex>;
}

export default function Designer() {
  const [model, setModel] = useState(emptyModel);
  const [tab, setTab] = useState('product');
  const [gen, setGen] = useState(null);           // { gea, issues, diagnostics }
  const [run, setRun] = useState(null);           // dry run result
  const [values, setValues] = useState({});       // what the user changed on the Try it tab
  const [texts, setTexts] = useState({});
  const [note, setNote] = useState(null);
  const [draft, setDraft] = useState(() => {      // an earlier draft offered on the first visit
    try { const d = JSON.parse(localStorage.getItem(DRAFT_KEY) || 'null'); return d && d.version === 1 ? d : null; } catch { return null; }
  });
  const [opened, setOpened] = useState(null);     // a file waiting for the user's choice, or a hand-written file shown as text
  const [showBlock, setShowBlock] = useState(false);
  const [busy, setBusy] = useState(false);
  const fileInput = useRef(null);
  const genSeq = useRef(0);
  const runSeq = useRef(0);

  // every edit goes through update(fn): a copy of the model is changed and stored
  const update = (fn) => setModel((m) => { const c = clone(m); fn(c); return c; });

  // keep the draft (the model only; nothing is stored while the first-visit question is open)
  useEffect(() => {
    if (draft) return;
    try { localStorage.setItem(DRAFT_KEY, JSON.stringify(model)); } catch { /* storage unavailable */ }
  }, [model, draft]);

  // the server generates the .gea and checks it with the real compiler
  useEffect(() => {
    if (draft) return undefined;
    const mine = ++genSeq.current;
    const t = setTimeout(async () => {
      try {
        const r = await postJson('/api/gea/designer/generate', { model });
        if (mine === genSeq.current) { setGen(r); setNote((n) => (n && n.startsWith('Server: ') ? null : n)); }
      } catch (e) { if (mine === genSeq.current) setNote('Server: ' + e.message); }
    }, 400);
    return () => clearTimeout(t);
  }, [model, draft]);

  // dry run on the Try it tab
  useEffect(() => {
    if (draft || tab !== 'try') return undefined;
    const mine = ++runSeq.current;
    const t = setTimeout(async () => {
      try {
        const r = await postJson('/api/gea/designer/dryrun', { model, values, texts });
        if (mine === runSeq.current) setRun(r);
      } catch (e) { if (mine === runSeq.current) setNote('Server: ' + e.message); }
    }, 400);
    return () => clearTimeout(t);
  }, [model, values, texts, tab, draft]);

  // ---------------------------------------------------------------- messages from the server, placed by path
  const all = [
    ...(gen?.issues || []).map((i) => ({ path: i.path, text: i.message, warn: i.isWarning })),
    ...(gen?.diagnostics || []).map((d) => ({ path: d.path, text: `${d.code} ${d.message}`, warn: d.severity === 'warning', line: d.line })),
  ];
  const at = (base) => all.filter((m) => m.path && (m.path === base || m.path.startsWith(base + '.')));
  const general = all.filter((m) => !m.path);
  const errorCount = all.filter((m) => !m.warn).length;

  // ---------------------------------------------------------------- file actions
  const download = () => {
    if (!gen?.gea) return;
    downloadText(`${safeFileName(model.product.name)}.gea`, gen.gea);
  };

  const onFile = async () => {
    const file = fileInput.current.files[0];
    fileInput.current.value = '';
    if (!file) return;
    setNote(null);
    try {
      const text = await file.text();
      const r = await postJson('/api/gea/designer/parse', { text });
      if (!r.hasModel) setOpened({ kind: 'text', name: file.name, text, reason: r.reason });
      else if (r.bodyChanged) setOpened({ kind: 'choose', name: file.name, text, model: r.model });
      else { loadModel(r.model); setOpened(null); }
    } catch (e) { setNote(`Could not open ${file.name}: ${e.message}`); }
  };

  const loadModel = (m) => {
    setModel(m); setValues({}); setTexts({}); setRun(null); setDraft(null); setTab('product');
  };

  const orderFromDesign = async () => {
    setBusy(true);
    try { await importScriptText(gen.gea, `${safeFileName(model.product.name)}.gea`); }
    catch (e) { setNote('Could not import the design: ' + e.message); setBusy(false); }
  };

  // ---------------------------------------------------------------- first visit with a saved draft
  if (draft) {
    return (
      <Card size="3">
        <Flex direction="column" gap="3">
          <Heading size="5">Product Designer</Heading>
          <Text size="2" color="gray">
            There is a draft from your last visit{draft.product?.name ? `: ${draft.product.name}` : ''}. Continue with it, or start a new design?
          </Text>
          <Flex gap="3">
            <Button onClick={() => { setModel(draft); setDraft(null); }}>Continue the last draft</Button>
            <Button variant="soft" color="gray" onClick={() => { setModel(emptyModel()); setDraft(null); }}>Start new</Button>
          </Flex>
        </Flex>
      </Card>
    );
  }

  // ---------------------------------------------------------------- tabs
  const prefix = model.product.prefix;

  const productTab = (
    <Flex direction="column" gap="3" mt="3" style={{ maxWidth: 520 }}>
      <Labeled label="Product name">
        <TextField.Root value={model.product.name} placeholder="Axial fan" onChange={(e) => update((m) => { m.product.name = e.target.value; })} />
      </Labeled>
      <Msgs items={at('product.name')} />
      <Labeled label="Name prefix (letters and digits, ending with _; every parameter name starts with it)">
        <TextField.Root value={prefix} placeholder="FN_" onChange={(e) => update((m) => { m.product.prefix = e.target.value; })} />
      </Labeled>
      <Msgs items={at('product.prefix')} />
      <Labeled label="Target"><Badge size="2" variant="soft">PART</Badge></Labeled>
      <Text size="1" color="gray">The Designer makes one part per design. Assemblies and drawings come in later versions.</Text>
      <Msgs items={at('product').filter((m) => m.path !== 'product.name' && m.path !== 'product.prefix')} />
    </Flex>
  );

  const paramRow = (g, p, gi, pi, group) => {
    const base = `groups[${gi}].params[${pi}]`;
    const set = (fn) => update((m) => fn(m.groups[gi].params[pi]));
    return (
      <Card key={pi} variant="surface" mb="2">
        <Flex direction="column" gap="2">
          <Flex gap="2" wrap="wrap" align="end">
            <Labeled label="Name"><Inp w={150} value={p.name} placeholder={`${prefix || 'XX_'}Name`} onChange={(v) => set((x) => { x.name = v; })} /></Labeled>
            <Labeled label="Label"><Inp w={160} value={p.label} onChange={(v) => set((x) => { x.label = v; })} /></Labeled>
            <Labeled label="Type"><Pick w={110} value={p.type} options={TYPES} onChange={(v) => update((m) => retype(m.groups[gi].params[pi], v))} /></Labeled>
            {UNITS[p.type] && <Labeled label="Unit"><Pick w={80} value={p.unit} options={UNITS[p.type]} onChange={(v) => set((x) => { x.unit = v; })} /></Labeled>}
            {p.type === 'CHECKBOX'
              ? <Labeled label="Default"><Pick w={90} value={p.default} options={['TRUE', 'FALSE']} onChange={(v) => set((x) => { x.default = v; })} /></Labeled>
              : p.type === 'RADIO'
                ? <Labeled label="Default"><Pick w={130} value={p.default} options={p.options || []} onChange={(v) => set((x) => { x.default = v; })} /></Labeled>
                : <Labeled label="Default"><Inp w={90} value={p.default} onChange={(v) => set((x) => { x.default = v; })} /></Labeled>}
            {NUMERIC.includes(p.type) && (
              <>
                <Labeled label="Min"><Inp w={80} type="number" value={p.min} onChange={(v) => set((x) => { const n = num(v); if (n === undefined) delete x.min; else x.min = n; })} /></Labeled>
                <Labeled label="Max"><Inp w={80} type="number" value={p.max} onChange={(v) => set((x) => { const n = num(v); if (n === undefined) delete x.max; else x.max = n; })} /></Labeled>
              </>
            )}
          </Flex>
          <Flex gap="2" wrap="wrap" align="end">
            {p.type === 'RADIO' && (
              <Labeled label="Options (comma separated)">
                <Inp w={220} value={(p.options || []).join(', ')}
                  onChange={(v) => set((x) => { x.options = v.split(',').map((s) => s.trim()).filter(Boolean); if (!x.options.includes(x.default)) x.default = x.options[0] || ''; })} />
              </Labeled>
            )}
            <Labeled label="Tooltip"><Inp w={220} value={p.tooltip} onChange={(v) => set((x) => { if (v) x.tooltip = v; else delete x.tooltip; })} /></Labeled>
            <Labeled label="Show only when… (expression)">
              <Inp w={200} value={p.visibleWhen} placeholder="e.g. FN_Guard" onChange={(v) => set((x) => { if (v) x.visibleWhen = v; else delete x.visibleWhen; })} />
            </Labeled>
            <Text as="label" size="2"><Flex gap="1" align="center"><Checkbox checked={!!p.required} onCheckedChange={(c) => set((x) => { if (c) x.required = true; else delete x.required; })} />Required</Flex></Text>
            <Flex gap="1" ml="auto">
              <Button size="1" variant="ghost" onClick={() => update((m) => move(m.groups[gi].params, pi, -1))} disabled={pi === 0} title="Move up">↑</Button>
              <Button size="1" variant="ghost" onClick={() => update((m) => move(m.groups[gi].params, pi, 1))} disabled={pi === group.params.length - 1} title="Move down">↓</Button>
              <Button size="1" variant="ghost" color="red" onClick={() => update((m) => { m.groups[gi].params.splice(pi, 1); })} title="Delete this parameter">Delete</Button>
            </Flex>
          </Flex>
          <Msgs items={at(base)} />
        </Flex>
      </Card>
    );
  };

  const paramsTab = (
    <Box mt="3">
      {model.groups.map((g, gi) => (
        <Card key={gi} mb="3">
          <Flex align="end" gap="2" mb="3" wrap="wrap">
            <Labeled label="Group title"><Inp w={240} value={g.title} onChange={(v) => update((m) => { m.groups[gi].title = v; })} /></Labeled>
            <Flex gap="1" ml="auto">
              <Button size="1" variant="ghost" disabled={gi === 0} onClick={() => update((m) => move(m.groups, gi, -1))} title="Move group up">↑</Button>
              <Button size="1" variant="ghost" disabled={gi === model.groups.length - 1} onClick={() => update((m) => move(m.groups, gi, 1))} title="Move group down">↓</Button>
              <Button size="1" variant="ghost" color="red" onClick={() => update((m) => { m.groups.splice(gi, 1); })}>Delete group</Button>
            </Flex>
          </Flex>
          <Msgs items={at(`groups[${gi}]`).filter((m) => !m.path.includes('.params['))} />
          {g.params.map((p, pi) => paramRow(g, p, gi, pi, g))}
          <Button size="1" variant="soft" onClick={() => update((m) => {
            const p = { name: prefix, type: 'LENGTH', default: '100', unit: 'mm', label: '' };
            m.groups[gi].params.push(p);
          })}>Add parameter</Button>
        </Card>
      ))}
      <Button variant="soft" onClick={() => update((m) => { m.groups.push({ title: `Group ${m.groups.length + 1}`, params: [] }); })}>Add group</Button>
    </Box>
  );

  const names = [
    ...model.groups.flatMap((g) => g.params.map((p) => `${p.name} (${p.type})`)),
    ...model.vars.map((v) => `${v.name} (${v.type})`),
  ];

  const namesHelp = (
    <Popover.Root>
      <Popover.Trigger><Button size="1" variant="ghost">Names you can use</Button></Popover.Trigger>
      <Popover.Content size="1" style={{ maxWidth: 360 }}>
        {names.length ? names.map((n, i) => <Text as="div" size="1" key={i}>{n}</Text>) : <Text size="1" color="gray">Add parameters first.</Text>}
        <Text as="div" size="1" color="gray" mt="2">Numbers with units: 5 mm, 30 deg. Compare like with like (a LENGTH with a LENGTH).</Text>
      </Popover.Content>
    </Popover.Root>
  );

  const rulesTab = (
    <Box mt="3">
      <Flex align="center" gap="3" mb="2"><Heading size="3">Variables</Heading>{namesHelp}</Flex>
      <Text as="p" size="1" color="gray" mb="2">A variable is calculated from the parameters and can be used in the rules below (it cannot be used in the Build text).</Text>
      {model.vars.map((v, i) => (
        <Card key={i} variant="surface" mb="2">
          <Flex gap="2" align="end" wrap="wrap">
            <Labeled label="Name"><Inp w={140} value={v.name} onChange={(x) => update((m) => { m.vars[i].name = x; })} /></Labeled>
            <Labeled label="Type"><Pick w={110} value={v.type} options={VAR_TYPES} onChange={(x) => update((m) => { m.vars[i].type = x; })} /></Labeled>
            <Labeled label="Expression"><Inp w={320} value={v.expr} placeholder="MIN(FN_Length, FN_Width)" onChange={(x) => update((m) => { m.vars[i].expr = x; })} /></Labeled>
            <Button size="1" variant="ghost" color="red" ml="auto" onClick={() => update((m) => { m.vars.splice(i, 1); })}>Delete</Button>
          </Flex>
          <Msgs items={at(`vars[${i}]`)} />
        </Card>
      ))}
      <Button size="1" variant="soft" mb="4" onClick={() => update((m) => { m.vars.push({ name: '', type: 'LENGTH', expr: '' }); })}>Add variable</Button>

      <Heading size="3" mb="2">Rules</Heading>
      <Text as="p" size="1" color="gray" mb="2">A rule is a condition that must be true. When it is false the user sees the message and cannot order. A warning only shows the message.</Text>
      {model.rules.map((r, i) => (
        <Card key={i} variant="surface" mb="2">
          <Flex gap="2" align="end" wrap="wrap">
            <Labeled label="Condition"><Inp w={300} value={r.expr} placeholder="FN_Wall < FN_Diameter / 2" onChange={(x) => update((m) => { m.rules[i].expr = x; })} /></Labeled>
            <Labeled label="Message shown when false"><Inp w={320} value={r.message} onChange={(x) => update((m) => { m.rules[i].message = x; })} /></Labeled>
            <Labeled label="When false"><Pick w={110} value={r.warn ? 'warning' : 'error'} options={['error', 'warning']} onChange={(x) => update((m) => { if (x === 'warning') m.rules[i].warn = true; else delete m.rules[i].warn; })} /></Labeled>
            <Button size="1" variant="ghost" color="red" ml="auto" onClick={() => update((m) => { m.rules.splice(i, 1); })}>Delete</Button>
          </Flex>
          <Msgs items={at(`rules[${i}]`)} />
        </Card>
      ))}
      <Button size="1" variant="soft" onClick={() => update((m) => { m.rules.push({ expr: '', message: '' }); })}>Add rule</Button>
    </Box>
  );

  const buildTab = (
    <Box mt="3">
      <Text as="p" size="2" color="gray" mb="2">
        Write the statements of the BUILD block (what goes between BEGIN_BUILD and END_BUILD). The parameters are available by name. A visual Parts tab arrives in a later version.
      </Text>
      {model.steps.length === 0 && (
        <Flex gap="2" mb="2">
          <Button size="1" variant="soft" onClick={() => update((m) => { m.steps.push({ kind: 'raw', text: '' }); })}>Add build text</Button>
        </Flex>
      )}
      {model.steps.map((s, i) => (
        <Box key={i} mb="3">
          <TextArea value={s.text} rows={18} spellCheck={false} style={{ fontFamily: 'var(--code-font-family, monospace)', fontSize: 13 }}
            onChange={(e) => update((m) => { m.steps[i].text = e.target.value; })} />
          <Msgs items={at(`steps[${i}]`)} />
          <Flex gap="2" mt="1">
            <Button size="1" variant="soft" onClick={() => update((m) => { m.steps[i].text = BOX_BUILD; })}>Insert the Box build as an example</Button>
            <Text size="1" color="gray">(the example uses the prefix BX_ and the Box parameters)</Text>
            {model.steps.length > 1 && <Button size="1" variant="ghost" color="red" onClick={() => update((m) => { m.steps.splice(i, 1); })}>Delete this block</Button>}
          </Flex>
        </Box>
      ))}
    </Box>
  );

  let body = gen?.gea || '';
  let block = '';
  const mark = body.lastIndexOf('\n!@DESIGNER');
  if (mark >= 0) {
    const start = body.lastIndexOf('\n! ---', mark);
    const cut = start >= 0 ? start : mark;
    block = body.slice(cut); body = body.slice(0, cut);
  }

  const textTab = (
    <Box mt="3">
      <Flex gap="2" mb="3" wrap="wrap" align="center">
        <Button onClick={download} disabled={!gen?.gea}>Download .gea</Button>
        <input ref={fileInput} type="file" accept=".gea,text/plain" hidden onChange={onFile} />
        <Button variant="outline" onClick={() => fileInput.current.click()}>Open .gea file…</Button>
        {!gen?.gea && <Text size="2" color="gray">Nothing to download yet: fix the problems listed on the other tabs.</Text>}
      </Flex>
      {opened?.kind === 'choose' && (
        <Notice color="amber">
          <div>{`${opened.name}: the text was edited by hand after the Designer wrote it.`}</div>
          <Flex gap="2" mt="2">
            <Button size="1" onClick={() => { loadModel(opened.model); setOpened(null); }}>Open the model (hand edits are dropped)</Button>
            <Button size="1" variant="soft" onClick={() => setOpened({ kind: 'text', name: opened.name, text: opened.text, reason: 'You chose to open it as text.' })}>Open as text</Button>
            <Button size="1" variant="ghost" onClick={() => setOpened(null)}>Cancel</Button>
          </Flex>
        </Notice>
      )}
      {opened?.kind === 'text' ? (
        <>
          <Notice color="amber">
            {`${opened.name}: ${opened.reason || 'This file was not made with the Designer.'} It can be imported on the Import page but not edited here.`}
          </Notice>
          <pre className="gea-text">{opened.text}</pre>
          <Button size="1" variant="ghost" onClick={() => setOpened(null)}>Back to the design</Button>
        </>
      ) : (
        <>
          <pre className="gea-text">{body || '(no text yet)'}</pre>
          {block && (
            <Box mt="2">
              <Button size="1" variant="ghost" onClick={() => setShowBlock((s) => !s)}>{showBlock ? 'Hide' : 'Show'} the model block</Button>
              {showBlock && <pre className="gea-text">{block}</pre>}
            </Box>
          )}
        </>
      )}
    </Box>
  );

  const form = run?.form;
  const tryTab = (
    <Box mt="3">
      {!run && <Text color="gray" size="2">Running…</Text>}
      {run && (
        <Flex direction="column" gap="3">
          <Flex gap="2" align="center">
            <Badge color={run.valid ? 'green' : 'red'} variant="soft" size="2">{run.valid ? 'valid' : 'not valid'}</Badge>
            <Text size="2" color="gray">{run.stage === 'build' ? 'The simulated build ran.' : run.stage === 'form' ? 'The values break a rule, so the build did not run.' : `Stopped at: ${run.stage}`}</Text>
          </Flex>
          <Msgs items={run.errors.map((e) => ({ text: e.message, line: e.line }))} />
          <Msgs items={run.warnings.map((e) => ({ text: e.message, warn: true }))} />
          {form && (
            <Card variant="surface">
              {[...new Set(form.fields.map((f) => f.group || ''))].map((g) => (
                <Box key={g || '_'} mb="2">
                  {g && <Heading size="3" mb="2">{g}</Heading>}
                  {form.fields.filter((f) => (f.group || '') === g && f.visible).map((f) => (
                    <ScriptField key={f.name} f={f}
                      value={f.name in values ? values[f.name] : f.value}
                      text={f.name in texts ? texts[f.name] : f.value}
                      invalid={f.errors.length > 0}
                      setValue={(v) => setValues((c) => ({ ...c, [f.name]: v }))}
                      setText={(v) => setTexts((c) => ({ ...c, [f.name]: v }))} />
                  ))}
                </Box>
              ))}
            </Card>
          )}
          {run.trace.length > 0 && (
            <details>
              <summary>{`Builder calls (${run.trace.length} steps)`}</summary>
              <pre className="gea-text">{run.trace.join('\n')}</pre>
            </details>
          )}
          <Text size="1" color="gray">Build preview is not available in this version: the dry run lists the builder calls with their values but makes no geometry.</Text>
          <Box>
            <Button disabled={!gen?.gea || errorCount > 0 || busy} onClick={orderFromDesign}>Order from this design</Button>
          </Box>
        </Flex>
      )}
    </Box>
  );

  return (
    <Card size="3">
      <Flex direction="column" gap="3">
        <Flex align="center" gap="3" wrap="wrap">
          <Heading size="5">Product Designer</Heading>
          <Text size="2" color="gray">{model.product.name || 'Untitled design'}</Text>
          {errorCount > 0 ? <Badge color="red" variant="soft">{`${errorCount} problem${errorCount === 1 ? '' : 's'}`}</Badge> : gen && <Badge color="green" variant="soft">no problems</Badge>}
          <Button size="1" variant="ghost" ml="auto" onClick={() => { if (window.confirm('Start a new design? The current one is replaced.')) loadModel(emptyModel()); }}>New design</Button>
        </Flex>
        <Notice color="amber">{note}</Notice>
        <Notice>{general.length ? general.map((m, i) => <div key={i}>{m.text}</div>) : null}</Notice>
        <Tabs.Root value={tab} onValueChange={setTab}>
          <Tabs.List>
            <Tabs.Trigger value="product">Product</Tabs.Trigger>
            <Tabs.Trigger value="params">Parameters</Tabs.Trigger>
            <Tabs.Trigger value="rules">Rules</Tabs.Trigger>
            <Tabs.Trigger value="build">Build (text)</Tabs.Trigger>
            <Tabs.Trigger value="text">Text</Tabs.Trigger>
            <Tabs.Trigger value="try">Try it</Tabs.Trigger>
          </Tabs.List>
          {tab === 'product' && productTab}
          {tab === 'params' && paramsTab}
          {tab === 'rules' && rulesTab}
          {tab === 'build' && buildTab}
          {tab === 'text' && textTab}
          {tab === 'try' && tryTab}
        </Tabs.Root>
      </Flex>
    </Card>
  );
}
