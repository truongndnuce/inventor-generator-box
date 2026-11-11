import { useMemo, useState } from 'react';
import { Badge, DropdownMenu, IconButton } from '@radix-ui/themes';
import { CubeIcon, DotsVerticalIcon, FileIcon, FileTextIcon, ImageIcon, ChevronDownIcon, ChevronLeftIcon, ChevronRightIcon, DoubleArrowLeftIcon, DoubleArrowRightIcon } from '@radix-ui/react-icons';
import { STATE_COLOR, fileUrl, fmtDate, go } from './lib.js';

const KIND_ICON = { image: ImageIcon, drawing: FileTextIcon, model: CubeIcon, other: FileIcon };

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

/** Row menu (⋮): open, download, delete. */
export function RowMenu({ item, onDelete }) {
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger>
        <IconButton variant="ghost" color="gray" aria-label="Actions" onClick={(e) => e.stopPropagation()}><DotsVerticalIcon /></IconButton>
      </DropdownMenu.Trigger>
      <DropdownMenu.Content onClick={(e) => e.stopPropagation()}>
        <DropdownMenu.Item onSelect={() => go(`#/item/${item.id}`)}>Open</DropdownMenu.Item>
        <DropdownMenu.Item asChild><a href={fileUrl(item.id, item.version, true)} download>Download</a></DropdownMenu.Item>
        <DropdownMenu.Separator />
        <DropdownMenu.Item color="red" onSelect={() => onDelete(item)}>Delete…</DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
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

export const FolderGlyph = () => <span style={{ color: 'var(--amber-9)' }}>📁</span>;

export function Tree({ folders, current, onSelect }) {
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

