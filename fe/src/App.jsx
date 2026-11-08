import { useState } from 'react';
import { Theme, Flex, Box, Text, Badge, Container, IconButton, Tooltip } from '@radix-ui/themes';
import { api, useHash, usePolling } from './lib.js';
import NewOrder from './pages/NewOrder.jsx';
import ScriptOrder from './pages/ScriptOrder.jsx';
import Orders from './pages/Orders.jsx';
import Dashboard from './pages/Dashboard.jsx';
import Job from './pages/Job.jsx';

const NAV = [
  { key: 'configurator', href: '#/', label: 'New order' },
  { key: 'orders', href: '#/orders', label: 'Ordered' },
  { key: 'dashboard', href: '#/dashboard', label: 'Dashboard' },
];

function WorkerPill() {
  const [state, setState] = useState({ on: false, text: 'worker: checking...' });
  usePolling(async () => {
    try {
      const online = (await api('/api/workers')).filter((w) => w.online);
      setState(online.length
        ? { on: true, text: `worker online: ${online.map((w) => w.name).join(', ')}` }
        : { on: false, text: 'no Inventor worker online' });
    } catch {
      setState({ on: false, text: 'server unreachable' });
    }
  }, 10000);
  return <Badge size="2" variant="soft" color={state.on ? 'green' : 'red'} title="Inventor worker status">{state.text}</Badge>;
}

export default function App() {
  const hash = useHash();
  const [dark, setDark] = useState(() => {
    try { return localStorage.getItem('cadflow.theme') === 'dark'; } catch { return false; }
  });
  // Trang job tự đặt mục nav theo trạng thái job (qua onNav): đang chờ/chạy thuộc Ordered, đã xong thuộc Dashboard.
  const [jobNav, setJobNav] = useState('orders');

  const active = hash.startsWith('#/orders') ? 'orders'
    : hash.startsWith('#/dashboard') ? 'dashboard'
    : hash.startsWith('#/jobs') ? jobNav
    : 'configurator';

  let page;
  if (hash.startsWith('#/jobs/')) page = <Job key={hash} id={decodeURIComponent(hash.slice(7))} onNav={setJobNav} />;
  else if (hash.startsWith('#/import/')) page = <ScriptOrder key={hash} scriptId={decodeURIComponent(hash.slice(9))} />;
  else if (hash.startsWith('#/orders')) page = <Orders />;
  else if (hash.startsWith('#/dashboard')) page = <Dashboard />;
  else page = <NewOrder key={hash} code={hash.startsWith('#/new/') ? decodeURIComponent(hash.slice(6)) : undefined} />;

  const toggle = () => {
    setDark((d) => {
      try { localStorage.setItem('cadflow.theme', d ? 'light' : 'dark'); } catch { /* bỏ qua */ }
      return !d;
    });
  };

  return (
    <Theme accentColor="teal" grayColor="slate" radius="medium" appearance={dark ? 'dark' : 'light'} panelBackground="solid">
      <header className="topbar">
        <Container size="4" px="4">
          <Flex align="center" gap="6" py="3">
            <a className="brand" href="#/">
              <Text weight="bold" size="4">CAD Automation</Text>
              <Text size="2" color="gray" ml="2">Assembly orders</Text>
            </a>
            <Flex gap="1" flexGrow="1" asChild>
              <nav>
                {NAV.map((n) => (
                  <a key={n.key} href={n.href} className={`navlink${active === n.key ? ' active' : ''}`}>{n.label}</a>
                ))}
              </nav>
            </Flex>
            <WorkerPill />
            <Tooltip content={dark ? 'Switch to light theme' : 'Switch to dark theme'}>
              <IconButton variant="ghost" color="gray" size="2" onClick={toggle} aria-label="Toggle theme">
                {dark ? '☀' : '☾'}
              </IconButton>
            </Tooltip>
          </Flex>
        </Container>
      </header>
      <Box asChild>
        <main>
          <Container size="4" px="4" py="5">{page}</Container>
        </main>
      </Box>
    </Theme>
  );
}
