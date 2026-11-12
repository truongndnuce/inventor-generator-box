import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import ModelView from '../ModelView.jsx';
import { AlertDialog, Button, Callout, ContextMenu, Dialog, Flex, Select, Text, TextField } from '@radix-ui/themes';
import { FileIcon, MagnifyingGlassIcon } from '@radix-ui/react-icons';
import { api, forgeViewable, go, send, uploadFile, fmtDate } from '../lib.js';
import { FolderGlyph, Crumbs, ModifiedCells, Pager, RenameDialog, RowMenu, StateBadge, Thumb, VersionBadge, usePaging } from '../components.jsx';

const ALL = '__all';
const STATES = ['In Work', 'Under Review', 'Approved', 'Released'];

export default function Files({ folderId, user, reloadTree, rev, allFolders = [] }) {
  // Height (px) of the file list above the viewer (null = as tall as its rows); drag the bar between them to change it.
  const [listH, setListH] = useState(() => { try { return Number(localStorage.getItem('plm.listH')) || null; } catch { return null; } });
  const page = useRef(null);
  const listBox = useRef(null);
  const startResize = (e) => {
    const box = page.current; if (!box || !listBox.current) return;
    e.preventDefault();
    const y0 = e.clientY, h0 = listBox.current.clientHeight, max = box.clientHeight - 280;
    let h = h0;
    const move = (ev) => { h = Math.max(90, Math.min(max, h0 + ev.clientY - y0)); setListH(h); };
    const up = () => {
      window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up);
      document.body.style.userSelect = '';
      try { localStorage.setItem('plm.listH', String(Math.round(h))); } catch { /* optional */ }
    };
    document.body.style.userSelect = 'none';
    window.addEventListener('pointermove', move); window.addEventListener('pointerup', up);
  };
  const [view, setView] = useState(null);
  const [ctx, setCtx] = useState(null);             // the row under the cursor when the context menu was opened (null = empty area)
  const [renaming, setRenaming] = useState(null);   // the row being renamed
  const [searchRev, setSearchRev] = useState(0);    // bumped to re-run the search after a rename
  const [error, setError] = useState('');
  const [busy, setBusy] = useState('');
  const [drag, setDrag] = useState(false);
  const [newName, setNewName] = useState(null);
  const [toDelete, setToDelete] = useState(null);
  const input = useRef(null);
  const [q, setQ] = useState('');
  const [kind, setKind] = useState(ALL);
  const [state, setState] = useState(ALL);
  const [found, setFound] = useState([]);
  const searching = !!q.trim() || kind !== ALL || state !== ALL;
  const [selected, setSelected] = useState(null);  // model file shown in 3D under the list
  const [picked, setPicked] = useState(null);       // properties of the part selected in the 3D view

  const load = useCallback(async () => {
    try {
      const v = await api(folderId ? `/api/vault/folders/${folderId}` : '/api/vault/folders');
      setView(v); setError(''); reloadTree?.();
    } catch (e) { setError(e.message); }
  }, [folderId]);
  useEffect(() => { load(); }, [load, rev]);

  // Search (all folders) replaces the folder listing while a text or a filter is set.
  useEffect(() => {
    if (!searching) return;
    const t = setTimeout(() => {
      const p = new URLSearchParams();
      if (q.trim()) p.set('q', q.trim());
      if (kind !== ALL) p.set('kind', kind);
      if (state !== ALL) p.set('state', state);
      api('/api/vault/search?' + p).then((r) => { setFound(r); setError(''); }).catch((e) => setError(e.message));
    }, 200);
    return () => clearTimeout(t);
  }, [q, kind, state, searching, searchRev]);

  const rows = useMemo(() => {
    if (searching) {
      // Folders whose name matches (from the whole tree), then the matching files.
      const text = q.trim().toLowerCase();
      const folders = text && kind === ALL && state === ALL
        ? allFolders.filter((f) => f.name.toLowerCase().includes(text)).sort((a, b) => a.name.localeCompare(b.name)).map((f) => ({ type: 'folder', f }))
        : [];
      return [...folders, ...found.map((i) => ({ type: 'item', i }))];
    }
    if (!view) return [];
    const folders = [...view.folders].sort((a, b) => a.name.localeCompare(b.name)).map((f) => ({ type: 'folder', f }));
    return [...folders, ...view.items.map((i) => ({ type: 'item', i }))];
  }, [view, searching, found, allFolders, q, kind, state]);
  const { rows: pageRows, pager } = usePaging(rows);
  // The selected model, with the freshest copy from the list (a rename or a new version changes it).
  const sel = selected && (rows.find((r) => r.type === 'item' && r.i.id === selected.id)?.i ?? selected);
  const pick = (i) => { setPicked(null); setSelected(i); };

  async function upload(files) {
    if (!folderId) { setError('Open a folder first, then upload.'); return; }
    const errors = [];
    for (const file of files) {
      setBusy(`Uploading ${file.name}…`);
      try { await uploadFile(`/api/vault/folders/${folderId}/items?name=${encodeURIComponent(file.name)}&by=${encodeURIComponent(user)}`, file); }
      catch (e) { errors.push(`${file.name}: ${e.message}`); }
    }
    setBusy(''); setError(errors.join('\n')); load();
  }

  async function createFolder() {
    try { await send('POST', '/api/vault/folders', { parentId: folderId, name: newName.trim(), createdBy: user }); setNewName(null); load(); }
    catch (e) { setError(e.message); }
  }

  async function remove() {
    try {
      await api(toDelete.type === 'folder' ? `/api/vault/folders/${toDelete.f.id}` : `/api/vault/items/${toDelete.i.id}`, { method: 'DELETE' });
      if (toDelete.type === 'item' && toDelete.i.id === selected?.id) setSelected(null);
      setToDelete(null); load();
    } catch (e) { setError(e.message); setToDelete(null); }
  }

  const folder = view?.folder;
  // With a file open the page fits the window and does not scroll: only the file list (and the folder tree) scroll.
  return (
    <div ref={page} style={sel ? { display: 'flex', flexDirection: 'column', height: 'calc(100vh - 104px)' } : undefined}>
      <h1 className="page-title">{folder ? folder.name : 'Files'}</h1>
      <div className="toolbar">
        <input ref={input} type="file" multiple hidden onChange={(e) => { upload([...e.target.files]); e.target.value = ''; }} />
        <TextField.Root style={{ width: 320 }} placeholder="Name" value={q} onChange={(e) => setQ(e.target.value)}>
          <TextField.Slot><MagnifyingGlassIcon /></TextField.Slot>
        </TextField.Root>
        <Select.Root value={kind} onValueChange={setKind}>
          <Select.Trigger /><Select.Content>
            <Select.Item value={ALL}>All types</Select.Item>
            {['image', 'drawing', 'model', 'bom', 'other'].map((k) => <Select.Item key={k} value={k}>{k}</Select.Item>)}
          </Select.Content>
        </Select.Root>
        <Select.Root value={state} onValueChange={setState}>
          <Select.Trigger /><Select.Content>
            <Select.Item value={ALL}>All states</Select.Item>
            {STATES.map((s) => <Select.Item key={s} value={s}>{s}</Select.Item>)}
          </Select.Content>
        </Select.Root>
        {busy && <Text size="2" color="gray">{busy}</Text>}
      </div>
      {error && <Callout.Root color="red" size="1" mb="3"><Callout.Text style={{ whiteSpace: 'pre-line' }}>{error}</Callout.Text></Callout.Root>}

      <ContextMenu.Root>
        <ContextMenu.Trigger>
        <div
          ref={listBox}
          style={sel ? { flex: listH ? `0 0 ${listH}px` : '0 1 auto', minHeight: 90, display: 'flex', flexDirection: 'column' } : { minHeight: 'calc(100vh - 230px)' }}
          onContextMenuCapture={() => setCtx(null)}
          onDragOver={(e) => { if (folderId) { e.preventDefault(); setDrag(true); } }}
          onDragLeave={() => setDrag(false)}
          onDrop={(e) => { e.preventDefault(); setDrag(false); if (folderId) upload([...e.dataTransfer.files]); }}
        >
          {searching ? <Text size="2" color="gray">Search results from all folders</Text> : <Crumbs items={view?.breadcrumb || []} />}
          {drag && <div className="dropzone">Drop files to upload to {folder?.name}</div>}
          <div style={sel ? { flex: '1 1 auto', minHeight: 0, overflow: 'auto', marginTop: 10 } : { marginTop: 10 }}>
          <table className="grid">
            <thead><tr><th /><th>Name</th><th>Version</th><th>State</th><th>Last modified</th><th>Modified by</th><th /></tr></thead>
            <tbody>
              {pageRows.map((r) => r.type === 'folder' ? (
                <tr key={r.f.id} className="row" onClick={() => go(`#/files/${r.f.id}`)} onContextMenu={() => setCtx(r)}>
                  <td className="thumb"><div className="thumb-box"><FolderGlyph /></div></td>
                  <td><b>{r.f.name}</b></td><td /><td />
                  <td>{fmtDate(r.f.createdAt)}</td><td>{r.f.createdBy}</td>
                  <td>{r.f.kind == null && <Button size="1" variant="ghost" color="gray" onClick={(e) => { e.stopPropagation(); setToDelete(r); }}>Delete</Button>}</td>
                </tr>
              ) : (
                <tr key={r.i.id} className="row" style={sel?.id === r.i.id ? { background: 'var(--accent-a3)' } : undefined}
                  onClick={() => (forgeViewable(r.i) ? pick(r.i) : go(`#/item/${r.i.id}`))} onContextMenu={() => setCtx(r)}>
                  <td className="thumb"><Thumb item={r.i} /></td>
                  <td>{r.i.name}</td><td><VersionBadge n={r.i.version} /></td><td><StateBadge state={r.i.state} /></td>
                  <ModifiedCells item={r.i} />
                  <td><RowMenu item={r.i} onDelete={(i) => setToDelete({ type: 'item', i })} /></td>
                </tr>
              ))}
              {view && rows.length === 0 && (
                <tr><td colSpan={7}style={{ padding: 40, textAlign: 'center', color: 'var(--gray-10)' }}>
                  <FileIcon width={28} height={28} /><br />{searching ? 'Nothing matches.' : folderId ? 'This folder is empty. Right-click to upload files, or drop them here.' : 'No folders yet. Create one, or finished jobs are saved here automatically.'}
                </td></tr>
              )}
            </tbody>
          </table>
          </div>
          <Pager {...pager} />
        </div>
        </ContextMenu.Trigger>
        <ContextMenu.Content>
          {ctx ? (
            // On a folder or a file row: rename it. The image / drawing / model / bom folders of a job are fixed.
            <ContextMenu.Item disabled={ctx.type === 'folder' && ctx.f.kind != null} onSelect={() => setRenaming(ctx)}>
              {ctx.type === 'folder' ? 'Rename folder…' : 'Rename file…'}
            </ContextMenu.Item>
          ) : (
            <>
              <ContextMenu.Item disabled={!folderId || !!busy} onSelect={() => input.current.click()}>Upload files…</ContextMenu.Item>
              <ContextMenu.Item onSelect={() => setNewName('')}>New folder…</ContextMenu.Item>
            </>
          )}
        </ContextMenu.Content>
      </ContextMenu.Root>

      {sel && (
        // Click a model in the list: its 3D view opens here, under the list, so another file is one click away.
        <>
        <div className="resize-bar" title="Drag to resize" onPointerDown={startResize} onDoubleClick={() => { setListH(null); try { localStorage.removeItem('plm.listH'); } catch { /* optional */ } }} />
        <div className="split-3d" style={{ flex: '1 1 0', minHeight: 160, marginTop: 0, gridTemplateRows: 'minmax(0, 1fr)' }}>
          <div className="viewer viewer-3d" style={{ height: '100%', minHeight: 0 }}>
            <ModelView key={`${sel.id}-${sel.version}`} item={sel} onProps={setPicked} />
          </div>
          <div className="card" style={{ overflow: 'auto', height: '100%', minHeight: 0 }}>
            <Flex justify="between" align="start" gap="2">
              <b style={{ fontSize: 13, wordBreak: 'break-all' }}>{sel.name}</b>
              <Button size="1" variant="ghost" color="gray" onClick={() => setSelected(null)}>Close</Button>
            </Flex>
            <dl className="kv" style={{ marginTop: 8 }}>
              <dt>Version</dt><dd><VersionBadge n={sel.version} /></dd>
              <dt>State</dt><dd><StateBadge state={sel.state} /></dd>
              <dt>Modified</dt><dd>{fmtDate(sel.modifiedAt)} by {sel.modifiedBy}</dd>
            </dl>
            <Button size="1" variant="soft" mt="2" onClick={() => go(`#/item/${sel.id}`)}>Details &amp; versions</Button>
            {picked && (
              <>
                <b style={{ fontSize: 13, display: 'block', marginTop: 14 }}>Selected part{picked.name ? `: ${picked.name}` : ''}</b>
                <dl className="kv" style={{ marginTop: 6 }}>
                  {picked.props.map((p, i) => <Fragment key={i}><dt>{p.displayName}</dt><dd>{String(p.displayValue)}{p.units ? ` ${p.units}` : ''}</dd></Fragment>)}
                </dl>
              </>
            )}
          </div>
        </div>
        </>
      )}

      {renaming && (
        <RenameDialog
          title={renaming.type === 'folder' ? 'Rename folder' : 'Rename file'}
          initial={renaming.type === 'folder' ? renaming.f.name : renaming.i.name}
          onClose={() => setRenaming(null)}
          onSubmit={async (name) => {
            if (renaming.type === 'folder') await send('PATCH', `/api/vault/folders/${renaming.f.id}`, { name });
            else await send('PATCH', `/api/vault/items/${renaming.i.id}`, { name, modifiedBy: user });
            await load();
            setSearchRev((n) => n + 1);   // search results show names too
          }} />
      )}

      <Dialog.Root open={newName !== null} onOpenChange={(o) => !o && setNewName(null)}>
        <Dialog.Content maxWidth="380px">
          <Dialog.Title>New folder</Dialog.Title>
          <TextField.Root autoFocus value={newName ?? ''} placeholder="Folder name" onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && newName.trim() && createFolder()} />
          <Flex gap="3" mt="4" justify="end">
            <Dialog.Close><Button variant="soft" color="gray">Cancel</Button></Dialog.Close>
            <Button disabled={!newName?.trim()} onClick={createFolder}>Create</Button>
          </Flex>
        </Dialog.Content>
      </Dialog.Root>

      <AlertDialog.Root open={!!toDelete} onOpenChange={(o) => !o && setToDelete(null)}>
        <AlertDialog.Content maxWidth="420px">
          <AlertDialog.Title>Delete {toDelete?.type === 'folder' ? 'folder' : 'file'}?</AlertDialog.Title>
          <AlertDialog.Description size="2">
            {toDelete?.type === 'folder'
              ? `“${toDelete.f.name}” and everything inside it, with all versions, will be deleted.`
              : `“${toDelete?.i.name}” and all its versions will be deleted.`} This cannot be undone.
          </AlertDialog.Description>
          <Flex gap="3" mt="4" justify="end">
            <AlertDialog.Cancel><Button variant="soft" color="gray">Cancel</Button></AlertDialog.Cancel>
            <AlertDialog.Action><Button color="red" onClick={remove}>Delete</Button></AlertDialog.Action>
          </Flex>
        </AlertDialog.Content>
      </AlertDialog.Root>
    </div>
  );
}
