import { useEffect, useRef, useState } from 'react';
import { Box, Button, Card, Checkbox, Flex, Heading, Select, Separator, Tabs, Text, TextField } from '@radix-ui/themes';
import { go, postJson } from '../lib.js';
import { Notice } from '../components.jsx';

const isText = (f) => f.kind === 'radio' || f.kind === 'text';

/** Một ô của form sinh từ khối FORM của script: radio -> Select, text, checkbox, hoặc số. */
function ScriptField({ f, value, text, invalid, setValue, setText }) {
  const label = <Text as="label" htmlFor={f.name} size="2" weight="medium" title={f.tooltip || ''}>{f.label}</Text>;
  const color = invalid ? 'red' : undefined;
  let control;
  let hint = null;
  if (f.kind === 'radio') {
    control = (
      <Select.Root value={text} onValueChange={setText}>
        <Select.Trigger id={f.name} color={color} />
        <Select.Content>{f.options.map((o) => <Select.Item key={o} value={o}>{o}</Select.Item>)}</Select.Content>
      </Select.Root>
    );
  } else if (f.kind === 'text') {
    control = <TextField.Root id={f.name} value={text} color={color} onChange={(e) => setText(e.target.value)} />;
  } else if (f.kind === 'checkbox') {
    control = <Checkbox id={f.name} checked={value === 1} color={color} onCheckedChange={(c) => setValue(c ? 1 : 0)} />;
  } else {
    control = (
      <TextField.Root id={f.name} type="number" min={f.min} max={f.max} step={f.integer ? 1 : (f.step || 'any')}
        value={value} color={color} onChange={(e) => e.target.value !== '' && setValue(Number(e.target.value))} />
    );
    hint = <span className="range">{f.min != null && f.max != null ? `${f.min}-${f.max} ${f.unit}`.trim() : f.unit}</span>;
  }
  return <div className="field script-field">{label}{control}{hint}</div>;
}

export default function ScriptOrder({ scriptId }) {
  const [state, setState] = useState(null);       // { imported, result }
  const [values, setValues] = useState({});       // ô số và ô tích (1/0)
  const [texts, setTexts] = useState({});         // ô text và ô chọn một
  const [result, setResult] = useState(null);
  const [note, setNote] = useState(null);
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState(false);
  const [who, setWho] = useState(() => localStorage.getItem('cadflow.user') || '');
  const seq = useRef(0);
  const ready = useRef(false);

  useEffect(() => {
    let alive = true;
    (async () => {
      let imported = {}; let retry = {};
      try { const s = JSON.parse(sessionStorage.getItem('cadflow.import') || '{}'); if (s.scriptId === scriptId) imported = s; } catch { /* bỏ qua */ }
      try { const s = JSON.parse(sessionStorage.getItem('cadflow.importRetry') || '{}'); if (s.scriptId === scriptId) retry = s; } catch { /* bỏ qua */ }
      sessionStorage.removeItem('cadflow.importRetry');
      const v = { ...(imported.values || {}), ...(retry.values || {}) };
      const t = { ...(imported.texts || {}), ...(retry.texts || {}) };
      try {
        const r = await postJson(`/api/gea/${scriptId}/form`, { values: v, texts: t });
        for (const f of r.form.fields) {
          if (isText(f)) { if (!(f.name in t)) t[f.name] = f.value; } else if (!(f.name in v)) v[f.name] = f.value;
        }
        if (!alive) return;
        setState({ imported });
        setValues(v); setTexts(t); setResult(r);
        ready.current = true;
      } catch (e) { if (alive) setNote('Could not load the script: ' + e.message); }
    })();
    return () => { alive = false; };
  }, [scriptId]);

  // Server tính lại ô ẩn/hiện và luật ASSERT mỗi khi giá trị đổi.
  useEffect(() => {
    if (!ready.current) return undefined;
    const mine = ++seq.current;
    setPending(true);
    const t = setTimeout(async () => {
      try {
        const r = await postJson(`/api/gea/${scriptId}/form`, { values, texts });
        if (mine === seq.current) { setResult(r); setPending(false); }
      } catch (e) { setNote('Could not evaluate the form: ' + e.message); }
    }, 250);
    return () => clearTimeout(t);
  }, [values, texts, scriptId]);

  if (!state || !result) return <Text color="gray">{note || 'Loading script...'}</Text>;

  const { imported } = state;
  const bad = new Set(result.form.errors.flatMap((e) => e.params));
  const warns = [...(imported.warnings || []), ...result.form.warnings.map((w) => w.message)];
  const groups = [...new Set(result.form.fields.map((f) => f.group || ''))];

  const submit = async () => {
    setBusy(true);
    try {
      localStorage.setItem('cadflow.user', who);
      const job = await postJson('/api/jobs', { scriptId, inputs: values, textInputs: texts, requestedBy: who });
      go(`#/jobs/${job.id}`);
    } catch (e) {
      setNote('Order rejected: ' + (e.body?.issues?.[0]?.message || e.message));
      setBusy(false);
    }
  };

  return (
    <Card size="3">
      <Flex direction="column" gap="4">
        <Tabs.Root value="imported">
          <Tabs.List>
            <Tabs.Trigger value="products" onClick={() => go('#/')}>Products</Tabs.Trigger>
            <Tabs.Trigger value="imported">Imported script</Tabs.Trigger>
          </Tabs.List>
        </Tabs.Root>

        <Box>
          <Heading size="5">{result.name || imported.fileName || 'Imported script'}</Heading>
          <Text as="p" size="2" color="gray" mt="1">
            This order runs the imported .gea script on the Inventor worker. The form below comes from the script&apos;s own FORM block; the server checks its rules again when you submit.
          </Text>
        </Box>

        <Notice color="amber">{note}</Notice>
        {warns.length > 0 && (
          <Notice color="amber"><ul style={{ margin: 0, paddingLeft: 18 }}>{warns.map((w, i) => <li key={i}>{w}</li>)}</ul></Notice>
        )}

        {groups.map((g) => (
          <Card key={g || '_'} variant="surface">
            {g && <Heading size="3" mb="2">{g}</Heading>}
            {result.form.fields.filter((f) => (f.group || '') === g && f.visible).map((f) => (
              <ScriptField key={f.name} f={f} value={values[f.name]} text={texts[f.name]}
                invalid={f.errors.length > 0 || bad.has(f.name)}
                setValue={(v) => setValues((c) => ({ ...c, [f.name]: v }))}
                setText={(v) => setTexts((c) => ({ ...c, [f.name]: v }))} />
            ))}
          </Card>
        ))}

        {result.form.notes.length > 0 && (
          <Box>{result.form.notes.map((n, i) => <Text as="div" size="2" color="gray" key={i}>{n}</Text>)}</Box>
        )}
        {result.form.errors.length > 0 && (
          <Notice><ul style={{ margin: 0, paddingLeft: 18 }}>{result.form.errors.map((e, i) => <li key={i}>{e.message}</li>)}</ul></Notice>
        )}

        <Separator size="4" />
        <Flex align="center" gap="3" wrap="wrap">
          <TextField.Root placeholder="Requested by" value={who} onChange={(e) => setWho(e.target.value)} style={{ minWidth: 240 }} />
          <Button size="2" disabled={!result.form.ok || busy || pending} onClick={submit}>Submit order</Button>
        </Flex>
      </Flex>
    </Card>
  );
}
