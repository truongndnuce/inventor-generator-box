import { useEffect, useRef, useState } from 'react';
import { Badge, Button, Callout, Card, Flex, Text, TextArea } from '@radix-ui/themes';
import { API, go, postJson } from '../lib.js';

const KEY = 'cadflow.chat';
const EXAMPLES = [
  'Tạo một gas turbine có 8 can, ống xả dài 1.5 m',
  'Box 300 x 200 x 120 mm, thành dày 3 mm',
  'Transformer mặc định, tank dài 2 m',
];

const load = () => {
  try { return JSON.parse(sessionStorage.getItem(KEY)) || []; } catch { return []; }
};

/** Đọc luồng SSE (data: {...}\n\n) từ POST /api/chat và gọi onEvent cho từng sự kiện. */
async function streamChat(body, onEvent, signal) {
  const res = await fetch(API + '/api/chat', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal,
  });
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try { msg = (await res.json()).error || msg; } catch { /* không phải JSON */ }
    throw new Error(res.status === 429 ? 'Too many requests. Please wait a moment.' : msg);
  }
  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let i;
    while ((i = buf.indexOf('\n\n')) >= 0) {
      const chunk = buf.slice(0, i);
      buf = buf.slice(i + 2);
      if (chunk.startsWith('data: ')) onEvent(JSON.parse(chunk.slice(6)));
    }
  }
}

function Proposal({ item, who, onChange }) {
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const rows = Object.entries(item.values || {});

  const create = async () => {
    setBusy(true); setErr('');
    try {
      const job = await postJson(`/api/chat/confirm/${item.draftId}`, { requestedBy: who });
      onChange({ ...item, jobId: job.id });
      go(`#/jobs/${job.id}`);
    } catch (e) {
      setErr(e.message); setBusy(false);
    }
  };
  const edit = () => {
    if (item.scriptId) {
      sessionStorage.setItem('cadflow.importRetry', JSON.stringify({ scriptId: item.scriptId, values: item.values, texts: {} }));
      go(`#/import/${item.scriptId}`);
    } else {
      sessionStorage.setItem('cadflow.retry', JSON.stringify(item.values));
      go(`#/new/${item.product}`);
    }
  };

  return (
    <Card className="chat-proposal">
      <Flex justify="between" align="center" mb="2">
        <Text weight="bold">Proposed job: {item.product}</Text>
        {item.jobId && <Badge color="green">created</Badge>}
      </Flex>
      {rows.length > 0 && (
        <table className="chat-values">
          <tbody>{rows.map(([k, v]) => <tr key={k}><td>{k}</td><td>{v}</td></tr>)}</tbody>
        </table>
      )}
      {err && <Callout.Root color="red" size="1" mt="2"><Callout.Text>{err}</Callout.Text></Callout.Root>}
      {item.jobId ? (
        <Button mt="3" variant="soft" onClick={() => go(`#/jobs/${item.jobId}`)}>Open job</Button>
      ) : (
        <Flex gap="2" mt="3">
          <Button onClick={create} loading={busy}>Create job</Button>
          <Button variant="soft" color="gray" onClick={edit} disabled={busy}>Edit in form</Button>
        </Flex>
      )}
    </Card>
  );
}

function Created({ item }) {
  const rows = Object.entries(item.values || {});
  return (
    <Card className="chat-proposal">
      <Flex justify="between" align="center" mb="2">
        <Text weight="bold">Job {item.jobId} ({item.product})</Text>
        <Badge color="green">queued</Badge>
      </Flex>
      {rows.length > 0 && (
        <details>
          <summary><Text size="2" color="gray">{rows.length} parameters</Text></summary>
          <table className="chat-values"><tbody>{rows.map(([k, v]) => <tr key={k}><td>{k}</td><td>{v}</td></tr>)}</tbody></table>
        </details>
      )}
      <Button mt="3" variant="soft" onClick={() => go(`#/jobs/${item.jobId}`)}>Open job</Button>
    </Card>
  );
}

export default function Chat() {
  // item: { role: 'user'|'assistant', text } | { role: 'proposal', ... } | { role: 'tool', name }
  const [items, setItems] = useState(load);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [who] = useState(() => { try { return localStorage.getItem('cadflow.user') || ''; } catch { return ''; } });
  const endRef = useRef(null);
  const abortRef = useRef(null);

  useEffect(() => {
    try { sessionStorage.setItem(KEY, JSON.stringify(items.filter((i) => i.role !== 'tool'))); } catch { /* bỏ qua */ }
    endRef.current?.scrollIntoView({ block: 'end' });
  }, [items]);
  useEffect(() => () => abortRef.current?.abort(), []);

  const send = async (text) => {
    text = text.trim();
    if (!text || busy) return;
    const next = [...items.filter((i) => i.role !== 'tool'), { role: 'user', text }];
    setItems(next); setInput(''); setBusy(true); setError('');
    // Chỉ gửi lời thoại user/assistant; thẻ đề xuất và dòng tool là việc của giao diện.
    const messages = next.filter((i) => i.role === 'user' || i.role === 'assistant').map((i) => ({ role: i.role, content: i.text }));
    const ctl = new AbortController();
    abortRef.current = ctl;
    try {
      await streamChat({ messages, requestedBy: who }, (e) => {
        if (e.type === 'text') setItems((l) => [...l.filter((i) => i.role !== 'tool'), { role: 'assistant', text: e.text }]);
        else if (e.type === 'tool') setItems((l) => [...l.filter((i) => i.role !== 'tool'), { role: 'tool', name: e.name }]);
        else if (e.type === 'job') setItems((l) => [...l.filter((i) => i.role !== 'tool'), { role: 'job', ...e }]);
        else if (e.type === 'proposal') setItems((l) => [...l.filter((i) => i.role !== 'tool'), { role: 'proposal', ...e }]);
        else if (e.type === 'error') setError(e.error);
      }, ctl.signal);
    } catch (e) {
      if (e.name !== 'AbortError') setError(e.message);
    } finally {
      setItems((l) => l.filter((i) => i.role !== 'tool'));
      setBusy(false);
    }
  };

  const reset = () => { abortRef.current?.abort(); setItems([]); setError(''); setBusy(false); };

  return (
    <div className="chat">
      <Flex justify="between" align="center" mb="3">
        <div>
          <Text as="div" size="5" weight="bold">Describe your job</Text>
          <Text as="div" size="2" color="gray">The assistant fills in the parameters, validates them and creates the job.</Text>
        </div>
        {items.length > 0 && <Button variant="soft" color="gray" onClick={reset}>New chat</Button>}
      </Flex>

      <div className="chat-log">
        {items.length === 0 && (
          <Flex direction="column" gap="2" align="start">
            <Text size="2" color="gray">Try:</Text>
            {EXAMPLES.map((ex) => <Button key={ex} variant="outline" size="2" onClick={() => send(ex)}>{ex}</Button>)}
          </Flex>
        )}
        {items.map((it, i) => {
          if (it.role === 'proposal') {
            return <Proposal key={i} item={it} who={who} onChange={(n) => setItems((l) => l.map((x, j) => (j === i ? n : x)))} />;
          }
          if (it.role === 'job') return <Created key={i} item={it} />;
          if (it.role === 'tool') return <Text key={i} size="1" color="gray" className="chat-tool">working: {it.name}...</Text>;
          return <div key={i} className={`chat-msg ${it.role}`}>{it.text}</div>;
        })}
        {busy && !items.some((i) => i.role === 'tool') && <Text size="1" color="gray">thinking...</Text>}
        <div ref={endRef} />
      </div>

      {error && <Callout.Root color="red" size="1" mb="2"><Callout.Text>{error}</Callout.Text></Callout.Root>}
      <Flex gap="2" align="end">
        <TextArea
          style={{ flex: 1 }} rows={2} value={input} placeholder="e.g. gas turbine with 8 cans and a 1.5 m exhaust"
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(input); } }}
          disabled={busy}
        />
        <Button onClick={() => send(input)} disabled={busy || !input.trim()}>Send</Button>
      </Flex>
    </div>
  );
}
