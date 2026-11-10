import { useRef } from 'react';
import { Badge, Button, Callout, Flex, Slider, Text, TextField } from '@radix-ui/themes';
import { API, STATUS_COLOR, importScriptText, reorder } from './lib.js';

/** Page title strip: a black background with the product image (public/hero.jpg) fading toward the left. */
const HERO_IMAGES = { 'gas-turbine': '/hero-turbine.jpg', generator: '/hero-generator.png', transformer: '/hero-transformer.png' };

export function Hero({ product, eyebrow, title, text, children }) {
  const img = HERO_IMAGES[product];
  return (
    <section className={`hero${img && img.endsWith('.png') ? ' cutout' : ''}${product === 'generator' ? ' wide' : ''}`} style={img ? { backgroundImage: `url('${img}')` } : { backgroundImage: 'none' }}>
      <div className="hero-copy">
        <span className="eyebrow">{eyebrow}</span>
        <h1>{title}</h1>
        {text && <p>{text}</p>}
        {children}
      </div>
    </section>
  );
}

/** One parameter: a number box + a slider kept in sync; the box turns red when a rule is violated (reported by the server). `note` shows under the label (e.g. changed from the original). */
export function ParamField({ p, value, invalid, onChange, note }) {
  const step = p.integer || p.max - p.min > 50 ? 1 : 0.1;
  return (
    <div className="field">
      <Text as="label" htmlFor={p.name} size="2" weight="medium">
        {p.label}
        {note && <Text as="div" size="1" color="gray">{note}</Text>}
      </Text>
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

export function StatusBadge({ status }) {
  return <Badge color={STATUS_COLOR[status] || 'gray'} variant="soft" radius="full">{status}</Badge>;
}

/** A box for errors/warnings; shows nothing when children is null. */
export function Notice({ color = 'red', children }) {
  if (!children || (Array.isArray(children) && children.length === 0)) return null;
  return (
    <Callout.Root color={color} size="1" mb="3" role="alert">
      <Callout.Text as="div">{children}</Callout.Text>
    </Callout.Root>
  );
}

/** Link to download the .gea file (the script used + the values) of a job; hidden if the job was not built from a script. */
export function ExportGeaLink({ job }) {
  if (!job.scriptSha256) return null;
  return (
    <Button asChild variant="soft" size="1" title="Download the .gea script with this job's values">
      <a href={`${API}/api/jobs/${encodeURIComponent(job.id)}/export.gea`} download={`${job.id}.gea`}>Export .gea</a>
    </Button>
  );
}

/** "Order again": pours the input of an old job into the New order form (shares the mechanism with "Edit and resubmit"). */
export function OrderAgainButton({ job, size = '1' }) {
  return <Button variant="soft" size={size} title="Open New order with this job's inputs" onClick={() => reorder(job)}>Order again</Button>;
}

/** The "Import .gea" button: sends the file to the server to compile it; on success it goes to the order-from-script page. */
export function ImportGeaButton({ onError }) {
  const input = useRef(null);
  const onChange = async () => {
    const file = input.current.files[0];
    input.current.value = '';
    if (!file) return;
    onError(null);
    try {
      const text = await file.text();
      await importScriptText(text, file.name);
    } catch (e) {
      const diags = (e.body?.diagnostics || []).filter((d) => d.severity === 'error').slice(0, 5);
      onError(
        <>
          <div>{`Could not import ${file.name}: ${e.message}`}</div>
          {diags.map((d, i) => <div key={i}><Text size="1" color="gray">{`line ${d.line}, col ${d.column}: ${d.code} ${d.message}`}</Text></div>)}
        </>,
      );
    }
  };
  return (
    <Flex ml="auto">
      <input ref={input} type="file" accept=".gea,text/plain" hidden onChange={onChange} />
      <Button variant="outline" title="Order from a .gea script (for example one exported from CAD Automation)" onClick={() => input.current.click()}>
        Import .gea
      </Button>
    </Flex>
  );
}


