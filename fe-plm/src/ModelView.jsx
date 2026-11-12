import { useCallback, useEffect, useRef, useState } from 'react';
import { Callout, Spinner } from '@radix-ui/themes';
import { api, send } from './lib.js';

const VIEWER = 'https://developer.api.autodesk.com/modelderivative/v2/viewers/7.*';
let sdk = null;

/** Loads the Autodesk Viewer SDK (script + css) once. */
function loadSdk() {
  if (window.Autodesk?.Viewing) return Promise.resolve();
  sdk ||= new Promise((resolve, reject) => {
    const css = document.createElement('link');
    css.rel = 'stylesheet'; css.href = `${VIEWER}/style.min.css`; document.head.appendChild(css);
    const s = document.createElement('script');
    s.src = `${VIEWER}/viewer3D.min.js`; s.onload = resolve;
    s.onerror = () => { sdk = null; reject(new Error('Could not load the Autodesk Viewer (is this computer online?).')); };
    document.head.appendChild(s);
  });
  return sdk;
}

/**
 * 3D view of an Inventor model through Autodesk Platform Services. The first time a file is opened it is uploaded and translated
 * (a minute or so); afterwards it opens straight away. onProps(list) receives the properties of the selected part.
 */
export default function ModelView({ item, onProps }) {
  const [st, setSt] = useState(null);
  const [error, setError] = useState('');
  const box = useRef(null);
  const viewer = useRef(null);
  const base = `/api/vault/items/${item.id}/aps`;
  const q = `?v=${item.version}`;

  const refresh = useCallback(() => api(base + q).then((s) => { setSt(s); setError(''); return s; }).catch((e) => { setError(e.message); }), [base, q]);
  useEffect(() => { refresh(); }, [refresh]);

  const translate = () => send('POST', base + '/translate' + q).then(setSt).catch((e) => setError(e.message));

  // Not prepared yet (e.g. a file uploaded by hand): send it to Autodesk straight away, no button to press.
  const started = useRef(false);
  useEffect(() => {
    if (st?.configured && st.status === 'notStarted' && !started.current) { started.current = true; translate(); }
  }, [st?.status, st?.configured]);

  // Poll while Autodesk is translating.
  useEffect(() => {
    if (!st || !['pending', 'inprogress'].includes(st.status)) return;
    const t = setInterval(refresh, 4000);
    return () => clearInterval(t);
  }, [st?.status, refresh]);

  // Show the model once it is translated.
  useEffect(() => {
    if (st?.status !== 'success' || !box.current) return;
    let dead = false;
    (async () => {
      try {
        await loadSdk();
        const A = window.Autodesk.Viewing;
        await new Promise((res) => A.Initializer({
          env: 'AutodeskProduction2', api: 'streamingV2',
          getAccessToken: (cb) => api(base + q).then((s) => cb(s.token, s.expiresIn)),
        }, res));
        if (dead) return;
        const v = new A.GuiViewer3D(box.current, { extensions: ['Autodesk.DocumentBrowser'] });
        viewer.current = v;
        v.start();
        v.addEventListener(A.SELECTION_CHANGED_EVENT, (e) => {
          const id = e.dbIdArray?.[0];
          if (!id) { onProps?.(null); return; }
          v.getProperties(id, (r) => onProps?.({ name: r.name, props: r.properties.filter((p) => !p.hidden).slice(0, 60) }), () => onProps?.(null));
        });
        A.Document.load('urn:' + st.urn, (doc) => {
          const node = doc.getRoot().getDefaultGeometry();
          v.loadDocumentNode(doc, node);
        }, (code, msg) => setError(`Could not open the model (${code}): ${msg}`));
      } catch (e) { setError(e.message); }
    })();
    return () => { dead = true; viewer.current?.finish(); viewer.current = null; };
  }, [st?.status, st?.urn]);

  if (error) return <Callout.Root color="red" size="1" m="3"><Callout.Text>{error}</Callout.Text></Callout.Root>;
  if (!st) return <Center><Spinner /></Center>;
  if (!st.configured) return <Center>3D viewing is not set up on the server (Aps:ClientId / Aps:ClientSecret).</Center>;
  if (st.status === 'notStarted') return <Center><Spinner /><p>Sending the file to Autodesk…</p></Center>;
  if (['pending', 'inprogress'].includes(st.status)) return <Center><Spinner /><p>Preparing the 3D view… {st.progress}</p></Center>;
  if (st.status === 'success') return <div ref={box} style={{ position: 'relative', width: '100%', height: '100%' }} />;
  return (
    <Center>
      <p style={{ color: 'var(--red-11)' }}>Autodesk could not prepare this file. {st.message}</p>
    </Center>
  );
}

const Center = ({ children }) => <div style={{ height: '100%', minHeight: 320, display: 'grid', placeContent: 'center', justifyItems: 'center', textAlign: 'center', color: 'var(--gray-11)', padding: 24 }}>{children}</div>;
