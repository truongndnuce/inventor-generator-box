import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertDialog, Button, Callout, ContextMenu, Dialog, Flex, Select, Text, TextField } from '@radix-ui/themes';
import { FileIcon, MagnifyingGlassIcon } from '@radix-ui/react-icons';
import { api, go, send, uploadFile, fmtDate } from '../lib.js';
import { FolderGlyph, Crumbs, ModifiedCells, Pager, RenameDialog, RowMenu, StateBadge, Thumb, VersionBadge, usePaging } from '../components.jsx';

const ALL = '__all';
const STATES = ['In Work', 'Under Review', 'Approved', 'Released'];

export default function Files({ folderId, user, reloadTree, rev }) {
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
    if (searching) return found.map((i) => ({ type: 'item', i }));
    if (!view) return [];
    const folders = [...view.folders].sort((a, b) => a.name.localeCompare(b.name)).map((f) => ({ type: 'folder', f }));
    return [...folders, ...view.items.map((i) => ({ type: 'item', i }))];
  }, [view, searching, found]);
  const { rows: pageRows, pager } = usePaging(rows);

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
      setToDelete(null); load();
    } catch (e) { setError(e.message); setToDelete(null); }
  }

  const folder = view?.folder;
  return (
    <>
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
          style={{ minHeight: 'calc(100vh - 230px)' }}
          onContextMenuCapture={() => setCtx(null)}
          onDragOver={(e) => { if (folderId) { e.preventDefault(); setDrag(true); } }}
          onDragLeave={() => setDrag(false)}
          onDrop={(e) => { e.preventDefault(); setDrag(false); if (folderId) upload([...e.dataTransfer.files]); }}
        >
          {searching ? <Text size="2" color="gray">Search results from all folders</Text> : <Crumbs items={view?.breadcrumb || []} />}
          {drag && <div className="dropzone">Drop files to upload to {folder?.name}</div>}
          <table className="grid" style={{ marginTop: 10 }}>
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
                <tr key={r.i.id} className="row" onClick={() => go(`#/item/${r.i.id}`)} onContextMenu={() => setCtx(r)}>
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
    </>
  );
}
