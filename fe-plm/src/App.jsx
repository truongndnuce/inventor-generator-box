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

  const [tree, setTree] = useState([]);
  const reloadTree = useCallback(() => { api('/api/vault/tree').then((t) => setTree(t.folders)).catch(() => {}); }, []);
  useEffect(() => { reloadTree(); }, [reloadTree]);

  const parts = hash.replace(/^#\/?/, '').split('/');
  const route = parts[0] || 'files';
  const page = route === 'item' ? <Item id={parts[1]} user={user} key={parts[1]} />
    : <Files folderId={parts[1] || null} user={user} reloadTree={reloadTree} key={parts[1] || 'root'} />;

  return (
    <Theme accentColor="blue" grayColor="slate" radius="small" appearance={dark ? 'dark' : 'light'}>
      <div className="shell">
        <header className="topbar">
          <div className="brand"><div className="brand-mark">Cf</div> Docs</div>
          <div className="project">Cadflow vault</div>
          <div className="spacer" />
          <a href={MAIN_APP} style={{ fontSize: 13 }}>Orders &amp; jobs</a>
          <a onClick={() => setDark(!dark)} style={{ fontSize: 13, cursor: 'pointer' }}>{dark ? 'Light' : 'Dark'}</a>
          <TextField.Root size="1" style={{ width: 150 }} placeholder="Your name" value={user} onChange={(e) => setUser(e.target.value)} />
        </header>
        <div className={`body ${collapsed ? 'collapsed' : ''}`}>
          <nav className="side">
            <a className="active" onClick={() => go('#/files')} style={{ cursor: 'pointer' }}><FileIcon /> <span>Files</span></a>
            {!collapsed && <Tree folders={tree} current={route === 'files' ? parts[1] || null : null} onSelect={(id) => go(id ? `#/files/${id}` : '#/files')} />}
            <div className="foot"><a onClick={() => setCollapsed(!collapsed)} style={{ cursor: 'pointer', padding: 8 }}>{collapsed ? <DoubleArrowRightIcon /> : <DoubleArrowLeftIcon />}</a></div>
          </nav>
          <main className="main">{page}</main>
        </div>
      </div>
    </Theme>
  );
}
