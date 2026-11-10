import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertDialog, Button, Callout, Dialog, Flex, Text, TextField } from '@radix-ui/themes';
import { ChevronDownIcon, ChevronRightIcon, FileIcon, MagnifyingGlassIcon, PlusIcon, UploadIcon } from '@radix-ui/react-icons';
import { api, go, send, uploadFile, fmtDate } from '../lib.js';
import { Crumbs, ModifiedCells, Pager, RowMenu, StateBadge, Thumb, VersionBadge, usePaging } from '../components.jsx';

const FolderGlyph = () => <span style={{ color: 'var(--amber-9)' }}>📁</span>;

function Tree({ folders, current, onSelect }) {
  const byParent = useMemo(() => {
    const m = new Map();
    for (const f of folders) { const k = f.parentId || ''; (m.get(k) || m.set(k, []).get(k)).push(f); }
    return m;
  }, [folders]);
  const ancestors = useMemo(() => {
    const set = new Set(); let f = folders.find((x) => x.id === current);
    while (f?.parentId) { set.add(f.parentId); f = folders.find((x) => x.id === f.parentId); }
    return set;
  }, [folders, current]);
  const [open, setOpen] = useState({});
  const isOpen = (id) => open[id] ?? ancestors.has(id);

  const Row = ({ f, depth }) => {
    const kids = byParent.get(f.id) || [];
    return (
      <>
        <div className={`tree-row ${current === f.id ? 'sel' : ''}`} style={{ paddingLeft: 8 + depth * 16 }} onClick={() => onSelect(f.id)}>
          <span className="tree-caret" onClick={(e) => { e.stopPropagation(); setOpen({ ...open, [f.id]: !isOpen(f.id) }); }}>
            {kids.length ? (isOpen(f.id) ? <ChevronDownIcon /> : <ChevronRightIcon />) : null}
          </span>
          <FolderGlyph /> <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.name}</span>
        </div>
        {isOpen(f.id) && kids.map((k) => <Row key={k.id} f={k} depth={depth + 1} />)}
      </>
    );
  };
  return (
    <div className="tree">
      <div className={`tree-row ${!current ? 'sel' : ''}`} onClick={() => onSelect(null)}><span className="tree-caret" /><FolderGlyph /> All files</div>
      {(byParent.get('') || []).map((f) => <Row key={f.id} f={f} depth={0} />)}
    </div>
  );
}

export default function Files({ folderId, user }) {
  const [tree, setTree] = useState([]);
  const [view, setView] = useState(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState('');
  const [drag, setDrag] = useState(false);
  const [newName, setNewName] = useState(null);
  const [toDelete, setToDelete] = useState(null);
  const input = useRef(null);

  const load = useCallback(async () => {
    try {
      const [t, v] = await Promise.all([api('/api/vault/tree'), api(folderId ? `/api/vault/folders/${folderId}` : '/api/vault/folders')]);
      setTree(t.folders); setView(v); setError('');
    } catch (e) { setError(e.message); }
  }, [folderId]);
  useEffect(() => { load(); }, [load]);

  const rows = useMemo(() => {
    if (!view) return [];
    const folders = [...view.folders].sort((a, b) => a.name.localeCompare(b.name)).map((f) => ({ type: 'folder', f }));
    return [...folders, ...view.items.map((i) => ({ type: 'item', i }))];
  }, [view]);
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
        <Button onClick={() => input.current.click()} disabled={!folderId || !!busy}><UploadIcon /> Upload</Button>
        <input ref={input} type="file" multiple hidden onChange={(e) => { upload([...e.target.files]); e.target.value = ''; }} />
        <Button variant="soft" onClick={() => setNewName('')}><PlusIcon /> New folder</Button>
        {busy && <Text size="2" color="gray">{busy}</Text>}
        <div className="spacer" />
        <Button variant="outline" color="gray" onClick={() => go('#/search/')}><MagnifyingGlassIcon /> Search and filter</Button>
      </div>
      {error && <Callout.Root color="red" size="1" mb="3"><Callout.Text style={{ whiteSpace: 'pre-line' }}>{error}</Callout.Text></Callout.Root>}

      <div className="files">
        <Tree folders={tree} current={folderId} onSelect={(id) => go(id ? `#/files/${id}` : '#/files')} />
        <div
          onDragOver={(e) => { if (folderId) { e.preventDefault(); setDrag(true); } }}
          onDragLeave={() => setDrag(false)}
          onDrop={(e) => { e.preventDefault(); setDrag(false); if (folderId) upload([...e.dataTransfer.files]); }}
        >
          <Crumbs items={view?.breadcrumb || []} />
          {drag && <div className="dropzone">Drop files to upload to {folder?.name}</div>}
          <table className="grid" style={{ marginTop: 10 }}>
            <thead><tr><th /><th>Number</th><th>Name</th><th>Version</th><th>State</th><th>Last modified</th><th>Modified by</th><th /></tr></thead>
            <tbody>
              {pageRows.map((r) => r.type === 'folder' ? (
                <tr key={r.f.id} className="row" onClick={() => go(`#/files/${r.f.id}`)}>
                  <td className="thumb"><div className="thumb-box"><FolderGlyph /></div></td>
                  <td>-</td><td><b>{r.f.name}</b></td><td /><td />
                  <td>{fmtDate(r.f.createdAt)}</td><td>{r.f.createdBy}</td>
                  <td>{r.f.kind == null && <Button size="1" variant="ghost" color="gray" onClick={(e) => { e.stopPropagation(); setToDelete(r); }}>Delete</Button>}</td>
                </tr>
              ) : (
                <tr key={r.i.id} className="row" onClick={() => go(`#/item/${r.i.id}`)}>
                  <td className="thumb"><Thumb item={r.i} /></td>
                  <td>{r.i.number}</td><td>{r.i.name}</td><td><VersionBadge n={r.i.version} /></td><td><StateBadge state={r.i.state} /></td>
                  <ModifiedCells item={r.i} />
                  <td><RowMenu item={r.i} onDelete={(i) => setToDelete({ type: 'item', i })} /></td>
                </tr>
              ))}
              {view && rows.length === 0 && (
                <tr><td colSpan={8} style={{ padding: 40, textAlign: 'center', color: 'var(--gray-10)' }}>
                  <FileIcon width={28} height={28} /><br />{folderId ? 'This folder is empty. Upload files, or drop them here.' : 'No folders yet. Create one, or use “Save to PLM” on a finished job.'}
                </td></tr>
              )}
            </tbody>
          </table>
          <Pager {...pager} />
        </div>
      </div>

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
