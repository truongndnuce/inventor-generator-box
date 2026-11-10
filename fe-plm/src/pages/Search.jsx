import { useEffect, useState } from 'react';
import { Select, TextField } from '@radix-ui/themes';
import { MagnifyingGlassIcon } from '@radix-ui/react-icons';
import { api, go } from '../lib.js';
import { ModifiedCells, Pager, StateBadge, Thumb, VersionBadge, usePaging } from '../components.jsx';

const ALL = '__all';
const STATES = ['In Work', 'Under Review', 'Approved', 'Released'];

export default function Search({ initial }) {
  const [q, setQ] = useState(initial);
  const [kind, setKind] = useState(ALL);
  const [state, setState] = useState(ALL);
  const [items, setItems] = useState([]);
  const [error, setError] = useState('');

  useEffect(() => {
    const t = setTimeout(() => {
      const p = new URLSearchParams();
      if (q.trim()) p.set('q', q.trim());
      if (kind !== ALL) p.set('kind', kind);
      if (state !== ALL) p.set('state', state);
      api('/api/vault/search?' + p).then((r) => { setItems(r); setError(''); }).catch((e) => setError(e.message));
    }, 200);
    return () => clearTimeout(t);
  }, [q, kind, state]);

  const { rows, pager } = usePaging(items);
  return (
    <>
      <h1 className="page-title">Search</h1>
      <div className="toolbar">
        <TextField.Root autoFocus style={{ width: 320 }} placeholder="Number or name" value={q} onChange={(e) => setQ(e.target.value)}>
          <TextField.Slot><MagnifyingGlassIcon /></TextField.Slot>
        </TextField.Root>
        <Select.Root value={kind} onValueChange={setKind}>
          <Select.Trigger /><Select.Content>
            <Select.Item value={ALL}>All types</Select.Item>
            {['image', 'drawing', 'model', 'other'].map((k) => <Select.Item key={k} value={k}>{k}</Select.Item>)}
          </Select.Content>
        </Select.Root>
        <Select.Root value={state} onValueChange={setState}>
          <Select.Trigger /><Select.Content>
            <Select.Item value={ALL}>All states</Select.Item>
            {STATES.map((s) => <Select.Item key={s} value={s}>{s}</Select.Item>)}
          </Select.Content>
        </Select.Root>
      </div>
      {error && <p style={{ color: 'var(--red-11)' }}>{error}</p>}
      <table className="grid">
        <thead><tr><th /><th>Number</th><th>Name</th><th>Version</th><th>State</th><th>Last modified</th><th>Modified by</th></tr></thead>
        <tbody>
          {rows.map((i) => (
            <tr key={i.id} className="row" onClick={() => go(`#/item/${i.id}`)}>
              <td className="thumb"><Thumb item={i} /></td><td>{i.number}</td><td>{i.name}</td>
              <td><VersionBadge n={i.version} /></td><td><StateBadge state={i.state} /></td><ModifiedCells item={i} />
            </tr>
          ))}
        </tbody>
      </table>
      <Pager {...pager} />
    </>
  );
}
