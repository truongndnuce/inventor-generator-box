import { useEffect, useRef, useState } from 'react';
import { Box, Button, Card, Flex, Heading, Select, Separator, Text, TextField } from '@radix-ui/themes';
import { api, go, postJson } from '../lib.js';
import { Hero, ImportGeaButton, Notice, ParamField, QuickPreview } from '../components.jsx';

export default function NewOrder({ code, configId }) {
  const [products, setProducts] = useState(null);
  const [configs, setConfigs] = useState([]); // the active Configurations of the product
  const [cfgId, setCfgId] = useState('');
  const [schema, setSchema] = useState(null);
  const [values, setValues] = useState({});
  const [issues, setIssues] = useState([]);
  const [bad, setBad] = useState(new Set());
  const [valid, setValid] = useState(false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState(null);
  const [who, setWho] = useState(() => localStorage.getItem('cadflow.user') || '');
  const [catalog, setCatalog] = useState({ materials: [], finishes: [] }); // Material / Finish choices (part information)
  const [material, setMaterial] = useState('');
  const [finish, setFinish] = useState('');
  const seq = useRef(0);

  useEffect(() => { api('/api/materials').then(setCatalog).catch(() => {}); }, []);
  // Part information sent with the order (only what the engineer chose).
  const partTexts = () => ({ ...(material ? { PI_Material: material } : {}), ...(finish ? { PI_Finish: finish } : {}) });

  // The product list and the schema of the selected product.
  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const list = await api('/api/products');
        const product = list.find((p) => p.code === code) || list[0];
        // Configuration: the one chosen in the URL (if still active), otherwise the default of the product.
        const all = (await api(`/api/products/${product.code}/configs`)).filter((c) => c.status === 'active');
        const chosen = all.find((c) => c.id === configId) || all.find((c) => c.isDefault) || null;
        const s = await api(`/api/products/${product.code}${chosen ? `?config=${chosen.id}` : ''}`);
        if (!alive) return;
        setConfigs(all); setCfgId(chosen ? chosen.id : '');
        const v = { ...s.defaults };
        // "Edit and resubmit" from a job: reload the old input (only keys that are in the schema).
        const retry = sessionStorage.getItem('cadflow.retry');
        sessionStorage.removeItem('cadflow.retry');
        if (retry) {
          try { for (const [k, x] of Object.entries(JSON.parse(retry))) if (k in v && Number.isFinite(x)) v[k] = x; } catch { /* ignore */ }
        }
        setProducts(list);
        setSchema({ ...s, description: product.description, name: product.name });
        setValues(v);
      } catch (e) { if (alive) setNote(e.message); }
    })();
    return () => { alive = false; };
  }, [code, configId]);

  // Check the rules on the server after every edit (wait 250 ms for typing to finish).
  useEffect(() => {
    if (!schema) return undefined;
    setValid(false);
    const mine = ++seq.current;
    const t = setTimeout(async () => {
      try {
        const r = await postJson(`/api/products/${schema.code}/validate`, { values });
        if (mine !== seq.current) return; // there is already a newer edit
        setValid(r.valid);
        setIssues(r.issues.map((i) => i.message));
        setBad(new Set(r.issues.flatMap((i) => i.params)));
      } catch (e) { setNote('Could not validate: ' + e.message); }
    }, 250);
    return () => clearTimeout(t);
  }, [values, schema]);

  if (!schema || !products) return <Text color="gray">{note || 'Loading products...'}</Text>;

  const submit = async () => {
    setBusy(true);
    try {
      localStorage.setItem('cadflow.user', who);
      const job = await postJson('/api/jobs', { productCode: schema.code, inputs: values, textInputs: partTexts(), requestedBy: who, configId: cfgId || undefined });
      go(`#/jobs/${job.id}`);
    } catch (e) {
      setNote('Order rejected: ' + (e.body?.issues?.[0]?.message || e.message));
      setBusy(false);
    }
  };

  return (
    <div className="order-page">
    <Hero product={schema.code} eyebrow="New order" title={schema.name} text={schema.description} />
    <Card size="3" className="order-card">
      <Flex direction="column" gap="4" className="order-flex">
        <Flex align="center" gap="3" wrap="wrap">
          <Text size="2" color="gray">Product</Text>
          <Select.Root value={schema.code} onValueChange={(v) => go(`#/new/${v}`)}>
            <Select.Trigger aria-label="Product" style={{ minWidth: 220 }} />
            <Select.Content>
              {products.map((p) => <Select.Item key={p.code} value={p.code}>{p.name}</Select.Item>)}
            </Select.Content>
          </Select.Root>
          <Text size="2" color="gray" ml="3">Configuration</Text>
          <Select.Root value={cfgId || undefined} onValueChange={(v) => go(`#/new/${schema.code}?config=${v}`)}>
            <Select.Trigger aria-label="Configuration" placeholder="Factory defaults" style={{ minWidth: 200 }} />
            <Select.Content>
              {configs.map((c) => <Select.Item key={c.id} value={c.id}>{c.name}{c.isDefault ? ' (default)' : ''}</Select.Item>)}
            </Select.Content>
          </Select.Root>
          <ImportGeaButton onError={setNote} />
        </Flex>

        <Box>
          <Text as="p" size="2" color="gray">
            Starting values come from the chosen configuration (see <a href={`#/config/${schema.code}`}>Config</a>). Rules are checked by the server; the same rules run again when the worker builds.
          </Text>
        </Box>

        <Notice color="amber">{note}</Notice>

        <Flex direction="column" gap="4" className="order-scroll">
          {catalog.materials.length > 0 && (
            <Card variant="surface">
              <Heading size="3" mb="2">Part information</Heading>
              <div className="field">
                <Text as="label" size="2" weight="medium">Material</Text>
                <Select.Root value={material || '-'} onValueChange={(v) => setMaterial(v === '-' ? '' : v)}>
                  <Select.Trigger aria-label="Material" style={{ minWidth: 260 }} />
                  <Select.Content>
                    <Select.Item value="-">Not specified</Select.Item>
                    {catalog.materials.map((m) => <Select.Item key={m.code} value={m.code}>{m.name}</Select.Item>)}
                  </Select.Content>
                </Select.Root>
                <span className="range">{material ? `${catalog.materials.find((m) => m.code === material)?.standard} - density ${catalog.materials.find((m) => m.code === material)?.density} g/cm3` : 'Written to the part properties and the BOM'}</span>
              </div>
              <div className="field">
                <Text as="label" size="2" weight="medium">Appearance / finish</Text>
                <Select.Root value={finish || '-'} onValueChange={(v) => setFinish(v === '-' ? '' : v)}>
                  <Select.Trigger aria-label="Appearance" style={{ minWidth: 260 }} />
                  <Select.Content>
                    <Select.Item value="-">Not specified</Select.Item>
                    {catalog.finishes.map((f) => <Select.Item key={f.code} value={f.code}>{f.name}</Select.Item>)}
                  </Select.Content>
                </Select.Root>
              </div>
            </Card>
          )}
          {schema.groups.map((g) => (
            <Card key={g} variant="surface">
              <Heading size="3" mb="2">{g}</Heading>
              {schema.parameters.filter((p) => p.group === g).map((p) => (
                <ParamField key={p.name} p={p} value={values[p.name]} invalid={bad.has(p.name)}
                  onChange={(v) => setValues((cur) => ({ ...cur, [p.name]: v }))} />
              ))}
            </Card>
          ))}
        </Flex>

        {issues.length > 0 && (
          <Box className="order-issues">
            <Notice>
              <ul style={{ margin: 0, paddingLeft: 18 }}>{issues.map((m, i) => <li key={i}>{m}</li>)}</ul>
            </Notice>
          </Box>
        )}

        <Separator size="4" />
        <Flex direction="column" gap="4" className="order-footer">
          <Flex align="center" gap="3" wrap="wrap">
            <TextField.Root placeholder="Requested by" value={who} onChange={(e) => setWho(e.target.value)} style={{ minWidth: 240 }} />
            <Button size="2" disabled={!valid || busy} onClick={submit}>Submit order</Button>
          </Flex>
          <QuickPreview disabled={!valid || busy}
            createJob={() => postJson('/api/jobs', { productCode: schema.code, inputs: values, requestedBy: who, configId: cfgId || undefined, kind: 'preview' })} />
        </Flex>
      </Flex>
    </Card>
    </div>
  );
}




