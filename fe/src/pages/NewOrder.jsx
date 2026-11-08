import { useEffect, useRef, useState } from 'react';
import { Box, Button, Card, Flex, Heading, Select, Separator, Slider, Text, TextField } from '@radix-ui/themes';
import { api, go, postJson } from '../lib.js';
import { ImportGeaButton, Notice } from '../components.jsx';

/** Một thông số: ô số + thanh trượt đồng bộ với nhau; ô đỏ khi vi phạm luật (do server báo). */
function ParamField({ p, value, invalid, onChange }) {
  const step = p.integer || p.max - p.min > 50 ? 1 : 0.1;
  return (
    <div className="field">
      <Text as="label" htmlFor={p.name} size="2" weight="medium">{p.label}</Text>
      <TextField.Root
        id={p.name} type="number" size="2" min={p.min} max={p.max} step={p.integer ? 1 : 'any'}
        value={value} color={invalid ? 'red' : undefined} variant={invalid ? 'soft' : 'surface'}
        onChange={(e) => e.target.value !== '' && onChange(Number(e.target.value))}
      />
      <Slider className="slider" size="1" min={p.min} max={p.max} step={step} value={[Math.min(p.max, Math.max(p.min, value))]}
        color={invalid ? 'red' : undefined} onValueChange={([v]) => onChange(v)} aria-label={p.label} />
      <span className="range">{`${p.min}-${p.max} ${p.unit}`.trim()}</span>
    </div>
  );
}

export default function NewOrder({ code }) {
  const [products, setProducts] = useState(null);
  const [schema, setSchema] = useState(null);
  const [values, setValues] = useState({});
  const [issues, setIssues] = useState([]);
  const [bad, setBad] = useState(new Set());
  const [valid, setValid] = useState(false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState(null);
  const [who, setWho] = useState(() => localStorage.getItem('cadflow.user') || '');
  const seq = useRef(0);

  // Danh sách sản phẩm và schema của sản phẩm đang chọn.
  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const list = await api('/api/products');
        const product = list.find((p) => p.code === code) || list[0];
        const s = await api(`/api/products/${product.code}`);
        if (!alive) return;
        const v = { ...s.defaults };
        // "Edit and resubmit" từ một job: nạp lại đầu vào cũ (chỉ lấy khóa có trong schema).
        const retry = sessionStorage.getItem('cadflow.retry');
        sessionStorage.removeItem('cadflow.retry');
        if (retry) {
          try { for (const [k, x] of Object.entries(JSON.parse(retry))) if (k in v && Number.isFinite(x)) v[k] = x; } catch { /* bỏ qua */ }
        }
        setProducts(list);
        setSchema({ ...s, description: product.description, name: product.name });
        setValues(v);
      } catch (e) { if (alive) setNote(e.message); }
    })();
    return () => { alive = false; };
  }, [code]);

  // Kiểm tra luật ở server sau mỗi lần sửa (đợi 250 ms cho gõ xong).
  useEffect(() => {
    if (!schema) return undefined;
    setValid(false);
    const mine = ++seq.current;
    const t = setTimeout(async () => {
      try {
        const r = await postJson(`/api/products/${schema.code}/validate`, { values });
        if (mine !== seq.current) return; // đã có lần sửa mới hơn
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
      const job = await postJson('/api/jobs', { productCode: schema.code, inputs: values, requestedBy: who });
      go(`#/jobs/${job.id}`);
    } catch (e) {
      setNote('Order rejected: ' + (e.body?.issues?.[0]?.message || e.message));
      setBusy(false);
    }
  };

  return (
    <Card size="3">
      <Flex direction="column" gap="4">
        <Flex align="center" gap="3" wrap="wrap">
          <Text size="2" color="gray">Product</Text>
          <Select.Root value={schema.code} onValueChange={(v) => go(`#/new/${v}`)}>
            <Select.Trigger aria-label="Product" style={{ minWidth: 220 }} />
            <Select.Content>
              {products.map((p) => <Select.Item key={p.code} value={p.code}>{p.name}</Select.Item>)}
            </Select.Content>
          </Select.Root>
          <ImportGeaButton onError={setNote} />
        </Flex>

        <Box>
          <Heading size="5">{schema.name}</Heading>
          <Text as="p" size="2" color="gray" mt="1">
            {schema.description} Rules are checked by the server; the same rules run again when the worker builds.
          </Text>
        </Box>

        <Notice color="amber">{note}</Notice>

        {schema.groups.map((g) => (
          <Card key={g} variant="surface">
            <Heading size="3" mb="2">{g}</Heading>
            {schema.parameters.filter((p) => p.group === g).map((p) => (
              <ParamField key={p.name} p={p} value={values[p.name]} invalid={bad.has(p.name)}
                onChange={(v) => setValues((cur) => ({ ...cur, [p.name]: v }))} />
            ))}
          </Card>
        ))}

        {issues.length > 0 && (
          <Notice>
            <ul style={{ margin: 0, paddingLeft: 18 }}>{issues.map((m, i) => <li key={i}>{m}</li>)}</ul>
          </Notice>
        )}

        <Separator size="4" />
        <Flex align="center" gap="3" wrap="wrap">
          <TextField.Root placeholder="Requested by" value={who} onChange={(e) => setWho(e.target.value)} style={{ minWidth: 240 }} />
          <Button size="2" disabled={!valid || busy} onClick={submit}>Submit order</Button>
        </Flex>
      </Flex>
    </Card>
  );
}
