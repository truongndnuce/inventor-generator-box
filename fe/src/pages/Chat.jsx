import { useEffect, useRef, useState } from 'react';
import { Badge, Button, Callout, Card, Flex, Text } from '@radix-ui/themes';
import { API, api, go, postJson, useHash } from '../lib.js';

const EXAMPLES = [
  'Create a gas turbine with 8 cans and a 1.5 m exhaust pipe',
  'Box 300 x 200 x 120 mm, wall thickness 3 mm',
  'Default transformer, tank 2 m long',
];

/** History lives in PostgreSQL (API /api/chats), filtered by user name. */
const ownerQ = (who) => `owner=${encodeURIComponent(who)}`;
const toItem = (i) => ({ role: i.role, ...(i.text != null ? { text: i.text } : {}), ...(i.data || {}) });

/** Groups conversations by time bucket like Claude: Today / Yesterday / Previous 7 days / Older. */
function groupChats(chats) {
  const day = 86400000;
  const start = new Date().setHours(0, 0, 0, 0);
  const groups = [['Today', []], ['Yesterday', []], ['Previous 7 days', []], ['Older', []]];
  for (const c of [...chats].sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt))) {
    const t = Date.parse(c.updatedAt);
    const g = t >= start ? 0 : t >= start - day ? 1 : t >= start - 7 * day ? 2 : 3;
    groups[g][1].push(c);
  }
  return groups.filter(([, l]) => l.length);
}

/** Reads the SSE stream (data: {...}\n\n) from POST /api/chat and calls onEvent for each event. */
async function streamChat(body, onEvent, signal) {
  const res = await fetch(API + '/api/chat', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal,
  });
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try { msg = (await res.json()).error || msg; } catch { /* not JSON */ }
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

function Sidebar({ chats, activeId, onSelect, onNew, onDelete, onRename, open }) {
  const [q, setQ] = useState('');
  const [editing, setEditing] = useState(null);
  const shown = q.trim() ? chats.filter((c) => c.title.toLowerCase().includes(q.trim().toLowerCase())) : chats;
  const groups = groupChats(shown);
  const commit = (c, v) => { setEditing(null); if (v.trim() && v.trim() !== c.title) onRename(c.id, v.trim()); };

  return (
    <aside className={`chat-side${open ? ' open' : ''}`}>
      <button className="chat-new" onClick={onNew}><span>＋</span> New chat</button>
      <input className="chat-search" placeholder="Search chats" value={q} onChange={(e) => setQ(e.target.value)} />
      <div className="chat-history">
        {groups.length === 0 && <div className="chat-empty-hist">{q ? 'No matching chats' : 'No chats yet'}</div>}
        {groups.map(([label, list]) => (
          <div key={label}>
            <div className="chat-group">{label}</div>
            {list.map((c) => (
              <div key={c.id} className={`chat-item${c.id === activeId ? ' active' : ''}`} onClick={() => onSelect(c.id)}
                onDoubleClick={() => setEditing(c.id)} title={c.title}>
                {editing === c.id ? (
                  <input autoFocus defaultValue={c.title} onClick={(e) => e.stopPropagation()}
                    onBlur={(e) => commit(c, e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') commit(c, e.target.value); if (e.key === 'Escape') setEditing(null); }} />
                ) : <span className="chat-item-title">{c.title}</span>}
                <button className="chat-del" aria-label="Delete chat" title="Delete chat"
                  onClick={(e) => { e.stopPropagation(); onDelete(c.id); }}>🗑</button>
              </div>
            ))}
          </div>
        ))}
      </div>
    </aside>
  );
}

export default function Chat() {
  // item: { role: 'user'|'assistant', text } | { role: 'proposal', ... } | { role: 'job', ... } | { role: 'tool', name }
  const hash = useHash();
  const activeId = hash.startsWith('#/chat/') ? decodeURIComponent(hash.slice(7)) : null;
  const [chats, setChats] = useState([]);
  const [items, setItems] = useState([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [sideOpen, setSideOpen] = useState(false);
  const [who] = useState(() => { try { return localStorage.getItem('cadflow.user') || ''; } catch { return ''; } });
  const endRef = useRef(null);
  const abortRef = useRef(null);
  const taRef = useRef(null);
  const loadedRef = useRef(null); // the conversation id that `items` currently holds (avoids reloading right after one was just created during streaming)

  const active = chats.find((c) => c.id === activeId);
  const refreshList = () => api(`/api/chats?${ownerQ(who)}`).then(setChats).catch((e) => setError(e.message));

  useEffect(() => { refreshList(); }, []);
  useEffect(() => {
    if (!activeId) { loadedRef.current = null; setItems([]); return; }
    if (loadedRef.current === activeId) return;
    let alive = true;
    setItems([]); setError('');
    api(`/api/chats/${activeId}?${ownerQ(who)}`)
      .then((d) => { if (alive) { loadedRef.current = activeId; setItems(d.items.map(toItem)); } })
      .catch(() => { if (alive) { setError('This chat no longer exists.'); go('#/chat'); } });
    return () => { alive = false; };
  }, [activeId]);
  useEffect(() => { endRef.current?.scrollIntoView({ block: 'end' }); }, [items, busy]);
  useEffect(() => () => abortRef.current?.abort(), []);
  useEffect(() => { // the textarea grows with its content
    const t = taRef.current;
    if (t) { t.style.height = 'auto'; t.style.height = Math.min(t.scrollHeight, 200) + 'px'; }
  }, [input]);

  const stop = () => { abortRef.current?.abort(); abortRef.current = null; setBusy(false); setItems((l) => l.filter((i) => i.role !== 'tool')); };
  const clean = (l) => l.filter((i) => i.role !== 'tool');

  const select = (id) => { stop(); setError(''); setSideOpen(false); go(`#/chat/${encodeURIComponent(id)}`); };
  const newChat = () => { stop(); setError(''); setInput(''); setSideOpen(false); go('#/chat'); };
  const remove = async (id) => {
    try { await api(`/api/chats/${id}?${ownerQ(who)}`, { method: 'DELETE' }); } catch (e) { setError(e.message); return; }
    setChats((l) => l.filter((c) => c.id !== id));
    if (id === activeId) newChat();
  };
  const rename = async (id, title) => {
    try {
      await api(`/api/chats/${id}?${ownerQ(who)}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title }) });
      setChats((l) => l.map((c) => (c.id === id ? { ...c, title } : c)));
    } catch (e) { setError(e.message); }
  };

  const send = async (text) => {
    text = text.trim();
    if (!text || busy) return;
    const next = [...clean(items), { role: 'user', text }];
    setItems(next); setInput(''); setBusy(true); setError('');
    // Send only the user/assistant utterances; proposal cards and tool lines are the UI's concern. The server saves to the DB itself.
    const messages = next.filter((i) => i.role === 'user' || i.role === 'assistant').map((i) => ({ role: i.role, content: i.text }));
    const ctl = new AbortController();
    abortRef.current = ctl;
    try {
      await streamChat({ messages, requestedBy: who, conversationId: activeId }, (e) => {
        if (e.type === 'conversation') {
          loadedRef.current = e.id; // items are already in state, no need to reload
          setChats((l) => [{ id: e.id, title: e.title, updatedAt: new Date().toISOString() }, ...l.filter((c) => c.id !== e.id)]);
          go(`#/chat/${encodeURIComponent(e.id)}`);
        } else if (e.type === 'text') setItems((l) => [...clean(l), { role: 'assistant', text: e.text }]);
        else if (e.type === 'tool') setItems((l) => [...clean(l), { role: 'tool', name: e.name }]);
        else if (e.type === 'job') setItems((l) => [...clean(l), { role: 'job', ...e }]);
        else if (e.type === 'proposal') setItems((l) => [...clean(l), { role: 'proposal', ...e }]);
        else if (e.type === 'error') setError(e.error);
      }, ctl.signal);
    } catch (e) {
      if (e.name !== 'AbortError') setError(e.message);
    } finally {
      if (abortRef.current === ctl) { setItems(clean); setBusy(false); }
      refreshList(); // update the order/time in the sidebar
    }
  };

  const tool = items.find((i) => i.role === 'tool');

  return (
    <div className="chat-shell">
      <Sidebar chats={chats} activeId={activeId} onSelect={select} onNew={newChat} onDelete={remove} onRename={rename} open={sideOpen} />
      <section className="chat-main">
        <div className="chat-bar">
          <button className="chat-burger" onClick={() => setSideOpen((o) => !o)} aria-label="Chats">☰</button>
          <span className="chat-bar-title">{active?.title || 'New chat'}</span>
        </div>

        <div className="chat-scroll">
          <div className="chat-col">
            {items.length === 0 && !activeId && (
              <div className="chat-hero">
                <div className="chat-hero-mark">✦</div>
                <h2>What do you want to build?</h2>
                <p>Describe a part. The assistant fills in the parameters, validates them and creates the job.</p>
                <div className="chat-chips">
                  {EXAMPLES.map((ex) => <button key={ex} onClick={() => send(ex)}>{ex}</button>)}
                </div>
              </div>
            )}
            {items.map((it, i) => {
              if (it.role === 'proposal') {
                return <Proposal key={i} item={it} who={who} onChange={(n) => setItems((l) => l.map((x, j) => (j === i ? n : x)))} />;
              }
              if (it.role === 'job') return <Created key={i} item={it} />;
              if (it.role === 'tool') return null;
              if (it.role === 'user') return <div key={i} className="chat-msg user">{it.text}</div>;
              return (
                <div key={i} className="chat-row">
                  <span className="chat-avatar">✦</span>
                  <div className="chat-msg assistant">{it.text}</div>
                </div>
              );
            })}
            {busy && (
              <div className="chat-row">
                <span className="chat-avatar pulse">✦</span>
                <span className="chat-status">{tool ? `Working: ${tool.name}…` : 'Thinking…'}</span>
              </div>
            )}
            {error && <Callout.Root color="red" size="1"><Callout.Text>{error}</Callout.Text></Callout.Root>}
            <div ref={endRef} />
          </div>
        </div>

        <div className="chat-composer-wrap">
          <div className="chat-composer">
            <textarea
              ref={taRef} rows={1} value={input} placeholder="Reply to the assistant…"
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); send(input); } }}
            />
            {busy
              ? <button className="chat-send stop" onClick={stop} aria-label="Stop">■</button>
              : <button className="chat-send" onClick={() => send(input)} disabled={!input.trim()} aria-label="Send">↑</button>}
          </div>
          <div className="chat-hint">Enter to send · Shift+Enter for a new line</div>
        </div>
      </section>
    </div>
  );
}
