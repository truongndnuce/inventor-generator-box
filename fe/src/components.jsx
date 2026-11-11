import { useRef, useState } from 'react';
import { Badge, Button, Callout, Checkbox, Flex, Select, Slider, Text, TextField } from '@radix-ui/themes';
import { API, STATUS_COLOR, api, fmtDuration, importScriptText, postJson, reorder, usePolling } from './lib.js';

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




const KIND_COLOR = { preview: 'violet', export: 'orange' };

/** The kind of a job (preview or export). A normal full build (generate) shows nothing, so most rows stay quiet. */
export function KindBadge({ kind }) {
  if (!kind || kind === 'generate') return null;
  return <Badge color={KIND_COLOR[kind] || 'gray'} variant="outline" radius="full" ml="2" title={kind === 'preview' ? 'Fast preview: one picture, no drawing' : 'Rebuild of an older job'}>{kind}</Badge>;
}

/** Text for a queued job: which queue it waits in and how many jobs are ahead of it. */
export function queueNote(job) {
  const n = job.queuePosition;
  const ahead = n == null ? '' : n === 0 ? ' (next in line)' : ` (${n} job${n === 1 ? '' : 's'} ahead)`;
  return `Waiting for an Inventor worker in the ${job.queue || 'build'} queue${ahead}...`;
}

/**
 * Quick preview: orders a `preview` job (fast, one picture) with createJob() and shows the picture right here when it is done.
 * Preview jobs wait in the filter queue, so they are not stuck behind full builds.
 */
export function QuickPreview({ createJob, disabled }) {
  const [job, setJob] = useState(null);
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const jobId = job?.id;
  const done = !job || ['succeeded', 'failed', 'cancelled'].includes(job.status);

  usePolling(async () => {
    if (!jobId || done) return;
    const j = await api(`/api/jobs/${encodeURIComponent(jobId)}`);
    setJob((cur) => (cur && cur.id === j.id ? j : cur)); // ignore a late answer for an older preview
  }, 1000, [jobId, done]);

  const start = async () => {
    setBusy(true); setError(null);
    try { setJob(await createJob()); }
    catch (e) { setError(e.body?.issues?.[0]?.message || e.message); }
    finally { setBusy(false); }
  };

  const image = job?.status === 'succeeded' ? job.artifacts.find((a) => a.kind === 'image') : null;
  const status = !job ? null : {
    queued: queueNote(job),
    running: `Building preview: ${job.step || '...'}`,
    succeeded: image ? null : 'Preview finished, but no picture was returned.',
    failed: 'Preview failed: ' + (job.error || 'unknown error'),
    cancelled: 'Preview was cancelled.',
  }[job.status];

  return (
    <Flex direction="column" gap="3">
      <Flex align="center" gap="3" wrap="wrap">
        <Button variant="soft" disabled={disabled || busy || (!done)} onClick={start}
          title="Build a fast picture of this configuration without waiting for the full build">Quick preview</Button>
        {status && <Text size="2" color={job.status === 'failed' ? 'red' : 'gray'}>{status}</Text>}
        {job && done && <Text size="2" color="gray"><a href={`#/jobs/${job.id}`}>{job.id}</a>{job.startedAt && job.finishedAt ? ` · ${fmtDuration((new Date(job.finishedAt) - new Date(job.startedAt)) / 1000)}` : ''}</Text>}
      </Flex>
      {error && <Notice color="amber">{error}</Notice>}
      {image && <img className="picture" src={`${API}/api/jobs/${encodeURIComponent(job.id)}/artifacts/${encodeURIComponent(image.name)}`} alt="Preview" />}
    </Flex>
  );
}

/** Depth of the build and filter queues (waiting, running, oldest wait, workers that take from it). */
export function QueueSummary() {
  const [queues, setQueues] = useState(null);
  usePolling(async () => setQueues(await api('/api/queues')), 3000);
  if (!queues) return null;
  return (
    <Flex gap="4" wrap="wrap" mb="4">
      {queues.map((q) => (
        <Text key={q.queue} size="2" color={q.queued > 0 && q.onlineWorkers === 0 ? 'red' : 'gray'}>
          <strong style={{ textTransform: 'capitalize' }}>{q.queue}</strong>
          {`: ${q.queued} waiting, ${q.running} running`}
          {q.oldestWaitSeconds != null ? `, oldest ${fmtDuration(q.oldestWaitSeconds)}` : ''}
          {`, ${q.onlineWorkers} worker${q.onlineWorkers === 1 ? '' : 's'}`}
        </Text>
      ))}
    </Flex>
  );
}

/** One field of the form generated from the FORM block of the script: radio -> Select, text, checkbox, or number. */
export function ScriptField({ f, value, text, invalid, setValue, setText }) {
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
