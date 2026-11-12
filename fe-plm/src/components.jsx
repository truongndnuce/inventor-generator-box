import { useEffect, useMemo, useState } from 'react';
import { Badge, Button, Callout, ContextMenu, Dialog, Flex, IconButton, TextField, Tooltip } from '@radix-ui/themes';
import { CubeIcon, DownloadIcon, EyeOpenIcon, TrashIcon, FileIcon, FileTextIcon, ImageIcon, MagnifyingGlassIcon, ChevronDownIcon, ChevronLeftIcon, ChevronRightIcon, DoubleArrowLeftIcon, DoubleArrowRightIcon } from '@radix-ui/react-icons';
import { STATE_COLOR, api, fileUrl, fmtDate, go, send } from './lib.js';

const KIND_ICON = { image: ImageIcon, drawing: FileTextIcon, model: CubeIcon, bom: FileTextIcon, other: FileIcon };

export function Thumb({ item }) {
  const Icon = KIND_ICON[item.kind] || FileIcon;
  return (
    <div className="thumb-box">
      {item.kind === 'image' ? <img src={fileUrl(item.id, item.version)} alt="" loading="lazy" /> : <Icon />}
    </div>
  );
}

export const StateBadge = ({ state }) => <Badge color={STATE_COLOR[state] || 'gray'} variant="soft">{state}</Badge>;
export const VersionBadge = ({ n }) => <span className="ver">{n}</span>;

export function Crumbs({ items, onRoot = '#/files' }) {
  return (
    <div className="crumbs">
      <a onClick={() => go(onRoot)}>Files</a>
      {items.map((f) => (
        <span key={f.id}>/ <a onClick={() => go(`#/files/${f.id}`)}>{f.name}</a></span>
      ))}
    </div>
  );
}

/** Row actions as three icon buttons: open, download, delete (each has a tooltip; clicks do not open the row). */
export function RowMenu({ item, onDelete }) {
  const stop = (e) => e.stopPropagation();
  return (
    <Flex gap="3" align="center" justify="end" onClick={stop}>
      <Tooltip content="Open">
        <IconButton variant="ghost" color="gray" aria-label="Open" onClick={() => go(`#/item/${item.id}`)}><EyeOpenIcon /></IconButton>
      </Tooltip>
      <Tooltip content="Download">
        <IconButton asChild variant="ghost" color="gray" aria-label="Download">
          <a href={fileUrl(item.id, item.version, true)} download><DownloadIcon /></a>
        </IconButton>
      </Tooltip>
      <Tooltip content="Delete">
        <IconButton variant="ghost" color="red" aria-label="Delete" onClick={() => onDelete(item)}><TrashIcon /></IconButton>
      </Tooltip>
    </Flex>
  );
}

export function Pager({ page, pages, total, from, to, onPage }) {
  return (
    <div className="pager">
      <span>{total === 0 ? 'No items' : `Showing ${from}–${to} of ${total}`}</span>
      <span style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
        <IconButton variant="ghost" color="gray" disabled={page <= 1} onClick={() => onPage(1)}><DoubleArrowLeftIcon /></IconButton>
        <IconButton variant="ghost" color="gray" disabled={page <= 1} onClick={() => onPage(page - 1)}><ChevronLeftIcon /></IconButton>
        <span>{page} of {pages}</span>
        <IconButton variant="ghost" color="gray" disabled={page >= pages} onClick={() => onPage(page + 1)}><ChevronRightIcon /></IconButton>
        <IconButton variant="ghost" color="gray" disabled={page >= pages} onClick={() => onPage(pages)}><DoubleArrowRightIcon /></IconButton>
      </span>
    </div>
  );
}

export function usePaging(list, size = 25) {
  const [page, setPage] = useState(1);
  const pages = Math.max(1, Math.ceil(list.length / size));
  const p = Math.min(page, pages);
  const from = list.length === 0 ? 0 : (p - 1) * size + 1;
  const to = Math.min(list.length, p * size);
  return { rows: list.slice((p - 1) * size, p * size), pager: { page: p, pages, total: list.length, from, to, onPage: setPage }, reset: () => setPage(1) };
}

export const ModifiedCells = ({ item }) => (<><td>{fmtDate(item.modifiedAt)}</td><td>{item.modifiedBy}</td></>);

/**
 * Rename dialog (folder or file). The name is pre-filled; for a file only the part before the extension is selected, since the extension must stay.
 * onSubmit(name) does the request and may throw: the message is shown in the dialog and it stays open.
 */
export function RenameDialog({ title, initial, onSubmit, onClose }) {
  const [name, setName] = useState(initial);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const unchanged = name.trim() === initial;

  async function save() {
    if (!name.trim() || unchanged || busy) return;
    setBusy(true);
    try { await onSubmit(name.trim()); onClose(); }
    catch (e) { setError(e.message); setBusy(false); }
  }

  return (
    <Dialog.Root open onOpenChange={(o) => !o && onClose()}>
      <Dialog.Content maxWidth="380px">
        <Dialog.Title>{title}</Dialog.Title>
        <TextField.Root autoFocus value={name} onChange={(e) => { setName(e.target.value); setError(''); }}
          onFocus={(e) => { const i = e.target.value.lastIndexOf('.'); e.target.setSelectionRange(0, title.includes('file') && i > 0 ? i : e.target.value.length); }}
          onKeyDown={(e) => e.key === 'Enter' && save()} />
        {error && <Callout.Root color="red" size="1" mt="3"><Callout.Text>{error}</Callout.Text></Callout.Root>}
        <Flex gap="3" mt="4" justify="end">
          <Dialog.Close><Button variant="soft" color="gray">Cancel</Button></Dialog.Close>
          <Button disabled={!name.trim() || unchanged || busy} onClick={save}>Rename</Button>
        </Flex>
      </Dialog.Content>
    </Dialog.Root>
  );
}

export const FolderGlyph = () => <span style={{ color: 'var(--amber-9)' }}>📁</span>;

/**
 * Folder tree of the sidebar. Every folder is expanded by default (the caret still collapses one).
 * Right-click a folder to create a sub-folder in it; right-click the empty area to create a top-level folder.
 */
export function Tree({ folders, current, onSelect, user, onCreated, onRenamed }) {
  const byParent = useMemo(() => {
    const m = new Map();
    for (const f of folders) { const k = f.parentId || ''; (m.get(k) || m.set(k, []).get(k)).push(f); }
    return m;
  }, [folders]);
  const [closed, setClosed] = useState({});
  const isOpen = (id) => !closed[id];

  // Context menu target: the folder under the cursor, or null for the empty area (a top-level folder).
  const [target, setTarget] = useState(null);
  const [newName, setNewName] = useState(null);
  const [error, setError] = useState('');
  const [renaming, setRenaming] = useState(null);   // the folder being renamed
  const parent = target ? folders.find((x) => x.id === target) : null;

  async function createFolder() {
    try {
      const created = await send('POST', '/api/vault/folders', { parentId: target, name: newName.trim(), createdBy: user });
      setNewName(null); setError('');
      onCreated?.(created);
    } catch (e) { setError(e.message); }
  }

  const Row = ({ f, depth }) => {
    const kids = byParent.get(f.id) || [];
    return (
      <>
        <div className={`tree-row ${current === f.id ? 'sel' : ''}`} style={{ paddingLeft: 8 + depth * 16 }}
          onClick={() => onSelect(f.id)} onContextMenu={() => setTarget(f.id)}>
          <span className="tree-caret" onClick={(e) => { e.stopPropagation(); setClosed({ ...closed, [f.id]: isOpen(f.id) }); }}>
            {kids.length ? (isOpen(f.id) ? <ChevronDownIcon /> : <ChevronRightIcon />) : null}
          </span>
          <FolderGlyph /> <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.name}</span>
        </div>
        {isOpen(f.id) && kids.map((k) => <Row key={k.id} f={k} depth={depth + 1} />)}
      </>
    );
  };
  // Search box: folders by name (from the tree) and files by name (from the server), across the whole vault.
  const [q, setQ] = useState('');
  const text = q.trim().toLowerCase();
  const [files, setFiles] = useState([]);
  useEffect(() => {
    if (!text) { setFiles([]); return; }
    const t = setTimeout(() => api('/api/vault/search?q=' + encodeURIComponent(q.trim())).then(setFiles).catch(() => setFiles([])), 200);
    return () => clearTimeout(t);
  }, [q]);
  const hits = text ? folders.filter((f) => f.name.toLowerCase().includes(text)) : [];
  const pathOf = (id) => { const names = []; for (let f = folders.find((x) => x.id === id); f; f = folders.find((x) => x.id === f.parentId)) names.unshift(f.name); return names.join(' / '); };

  return (
    <>
      <div style={{ padding: '6px 8px' }}>
        <TextField.Root size="1" placeholder="Search files & folders" value={q} onChange={(e) => setQ(e.target.value)}>
          <TextField.Slot><MagnifyingGlassIcon /></TextField.Slot>
        </TextField.Root>
      </div>
      <ContextMenu.Root>
        <ContextMenu.Trigger>
          {/* Capture runs before the row's own handler, so a row overrides this reset to null. */}
          <div className="tree" onContextMenuCapture={() => setTarget(null)}>
            {text ? (
              <>
                {hits.map((f) => (
                  <div key={f.id} className="tree-row" title={pathOf(f.id)} style={{ paddingLeft: 12 }} onClick={() => onSelect(f.id)}>
                    <FolderGlyph /> <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.name}</span>
                  </div>
                ))}
                {files.map((i) => (
                  <div key={i.id} className="tree-row" title={pathOf(i.folderId)} style={{ paddingLeft: 12 }} onClick={() => go(`#/files/${i.folderId}`)}>
                    <FileIcon /> <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{i.name}</span>
                  </div>
                ))}
                {!hits.length && !files.length && <div style={{ padding: 12, color: 'var(--gray-10)', fontSize: 13 }}>Nothing matches.</div>}
              </>
            ) : (byParent.get('') || []).map((f) => <Row key={f.id} f={f} depth={0} />)}
          </div>
        </ContextMenu.Trigger>
        <ContextMenu.Content>
          <ContextMenu.Item onSelect={() => { setError(''); setNewName(''); }}>
            {parent ? `New folder in “${parent.name}”…` : 'New folder…'}
          </ContextMenu.Item>
          {parent && (
            // The image / drawing / model / bom folders of a job are fixed and cannot be renamed.
            <ContextMenu.Item disabled={parent.kind != null} onSelect={() => setRenaming(parent)}>Rename folder…</ContextMenu.Item>
          )}
        </ContextMenu.Content>
      </ContextMenu.Root>

      {renaming && (
        <RenameDialog title="Rename folder" initial={renaming.name} onClose={() => setRenaming(null)}
          onSubmit={async (name) => { await send('PATCH', `/api/vault/folders/${renaming.id}`, { name }); onRenamed?.(); }} />
      )}

      <Dialog.Root open={newName !== null} onOpenChange={(o) => !o && setNewName(null)}>
        <Dialog.Content maxWidth="380px">
          <Dialog.Title>New folder</Dialog.Title>
          <Dialog.Description size="2" color="gray" mb="3">{parent ? `Inside “${parent.name}”` : 'At the top level'}</Dialog.Description>
          <TextField.Root autoFocus value={newName ?? ''} placeholder="Folder name" onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && newName.trim() && createFolder()} />
          {error && <Callout.Root color="red" size="1" mt="3"><Callout.Text>{error}</Callout.Text></Callout.Root>}
          <Flex gap="3" mt="4" justify="end">
            <Dialog.Close><Button variant="soft" color="gray">Cancel</Button></Dialog.Close>
            <Button disabled={!newName?.trim()} onClick={createFolder}>Create</Button>
          </Flex>
        </Dialog.Content>
      </Dialog.Root>
    </>
  );
}

