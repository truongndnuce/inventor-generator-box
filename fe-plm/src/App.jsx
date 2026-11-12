import { useCallback, useEffect, useState } from 'react';
import { Theme, TextField } from '@radix-ui/themes';
import { FileIcon, DoubleArrowLeftIcon, DoubleArrowRightIcon } from '@radix-ui/react-icons';
import { MAIN_APP, api, go, useHash, useUser } from './lib.js';
import { Tree } from './components.jsx';
import Files from './pages/Files.jsx';
import Item from './pages/Item.jsx';

export default function App() {
  const hash = useHash();
  const [user, setUser] = useUser();
  const [collapsed, setCollapsed] = useState(false);
  const [dark, setDark] = useState(() => { try { return localStorage.getItem('plm.dark') === '1'; } catch { return false; } });
  useEffect(() => { try { localStorage.setItem('plm.dark', dark ? '1' : '0'); } catch { /* ignore */ } }, [dark]);

  // Width of the folder sidebar; drag its right edge.
  const [sideW, setSideW] = useState(() => { try { return Number(localStorage.getItem('plm.sideW')) || 260; } catch { return 260; } });
  const startSide = (e) => {
    e.preventDefault();
    const x0 = e.clientX, w0 = sideW; let w = w0;
    const move = (ev) => { w = Math.max(160, Math.min(640, w0 + ev.clientX - x0)); setSideW(w); };
    const up = () => {
      window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up);
      document.body.style.userSelect = '';
      try { localStorage.setItem('plm.sideW', String(Math.round(w))); } catch { /* optional */ }
    };
    document.body.style.userSelect = 'none';
    window.addEventListener('pointermove', move); window.addEventListener('pointerup', up);
  };

  const [tree, setTree] = useState([]);
  const [rev, setRev] = useState(0);   // bumped when the sidebar renames a folder, so the open listing reloads
  const reloadTree = useCallback(() => { api('/api/vault/tree').then((t) => setTree(t.folders)).catch(() => {}); }, []);
  useEffect(() => { reloadTree(); }, [reloadTree]);

  const parts = hash.replace(/^#\/?/, '').split('/');
  const route = parts[0] || 'files';
  const page = route === 'item' ? <Item id={parts[1]} user={user} key={parts[1]} />
    : <Files folderId={parts[1] || null} user={user} reloadTree={reloadTree} rev={rev} allFolders={tree} key={parts[1] || 'root'} />;

  return (
    <Theme accentColor="blue" grayColor="slate" radius="small" appearance={dark ? 'dark' : 'light'}>
      <div className="shell">
        <header className="topbar">
          <div className="brand"><div className="brand-mark">SV</div> Smart Vault</div>
          <div className="spacer" />
          <a href={MAIN_APP} style={{ fontSize: 13 }}>Orders &amp; jobs</a>
          <a onClick={() => setDark(!dark)} style={{ fontSize: 13, cursor: 'pointer' }}>{dark ? 'Light' : 'Dark'}</a>
          <TextField.Root size="1" style={{ width: 150 }} placeholder="Your name" value={user} onChange={(e) => setUser(e.target.value)} />
        </header>
        <div className={`body ${collapsed ? 'collapsed' : ''}`} style={{ '--plm-side': `${sideW}px` }}>
          <nav className="side">
            <a className="active" onClick={() => go('#/files')} style={{ cursor: 'pointer' }}><FileIcon /> <span>Files</span></a>
            {!collapsed && <Tree folders={tree} current={route === 'files' ? parts[1] || null : null} user={user}
              onSelect={(id) => go(id ? `#/files/${id}` : '#/files')}
              onCreated={(f) => { reloadTree(); go(`#/files/${f.id}`); }}
              onRenamed={() => { reloadTree(); setRev((r) => r + 1); }} />}
            <div className="foot"><a onClick={() => setCollapsed(!collapsed)} style={{ cursor: 'pointer', padding: 8 }}>{collapsed ? <DoubleArrowRightIcon /> : <DoubleArrowLeftIcon />}</a></div>
            {!collapsed && <div className="side-resize" title="Drag to resize" onPointerDown={startSide} onDoubleClick={() => setSideW(260)} />}
          </nav>
          <main className="main">{page}</main>
        </div>
      </div>
    </Theme>
  );
}
