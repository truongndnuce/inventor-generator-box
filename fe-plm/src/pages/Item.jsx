import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import { Badge, Button, Callout, Select } from '@radix-ui/themes';
import { DownloadIcon, UploadIcon } from '@radix-ui/react-icons';
import { api, forgeViewable, fileUrl, fmtDate, fmtSize, go, MAIN_APP, send, uploadFile } from '../lib.js';
import { Crumbs, StateBadge, VersionBadge } from '../components.jsx';
import BomView from '../BomView.jsx';
import ModelView from '../ModelView.jsx';

const NEXT = { 'In Work': ['Under Review'], 'Under Review': ['In Work', 'Approved'], Approved: ['In Work', 'Released'], Released: [] };

function Preview({ item, onProps }) {
  const url = fileUrl(item.id, item.version);
  if (forgeViewable(item)) return <ModelView item={item} onProps={onProps} />;
  if (item.kind === 'bom') return <BomView item={item} />;
  if (item.kind === 'image') return <img src={url} alt={item.name} />;
  if (item.contentType === 'application/pdf') return <iframe src={url} title={item.name} />;
  return <div style={{ padding: 40, textAlign: 'center', color: 'var(--gray-10)' }}>No preview for {item.name.split('.').pop().toUpperCase()} files.<br />Download it to open in CAD.</div>;
}

export default function Item({ id, user }) {
  const [data, setData] = useState(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [picked, setPicked] = useState(null);   // properties of the part selected in the 3D view
  const input = useRef(null);

  const load = useCallback(() => api(`/api/vault/items/${id}`).then((d) => { setData(d); setError(''); }).catch((e) => setError(e.message)), [id]);
  useEffect(() => { load(); }, [load]);

  async function setState(state) {
    try { await send('PATCH', `/api/vault/items/${id}/state`, { state, modifiedBy: user }); load(); } catch (e) { setError(e.message); }
  }
  async function checkIn(file) {
    setBusy(true);
    try { await uploadFile(`/api/vault/items/${id}/versions?by=${encodeURIComponent(user)}`, file); await load(); }
    catch (e) { setError(e.message); }
    setBusy(false);
  }

  if (!data) return error ? <Callout.Root color="red"><Callout.Text>{error}</Callout.Text></Callout.Root> : null;
  const { item, versions, breadcrumb } = data;
  const jobId = item.jobId || versions.find((v) => v.sourceJobId)?.sourceJobId;
  return (
    <>
      <Crumbs items={breadcrumb} />
      <h1 className="page-title" style={{ marginTop: 10 }}>{item.name}</h1>
      {error && <Callout.Root color="red" size="1" mb="3"><Callout.Text>{error}</Callout.Text></Callout.Root>}
      <div className="detail">
        <div className={`viewer ${forgeViewable(item) ? 'viewer-3d' : ''}`}><Preview item={{ ...item, contentType: item.contentType }} onProps={setPicked} /></div>
        <div style={{ display: 'grid', gap: 16 }}>
          {forgeViewable(item) && picked && (
            <div className="card">
              <b style={{ fontSize: 13 }}>Selected part{picked.name ? `: ${picked.name}` : ''}</b>
              <dl className="kv" style={{ maxHeight: 260, overflow: 'auto', marginTop: 8 }}>
                {picked.props.map((p, i) => <Fragment key={i}><dt>{p.displayName}</dt><dd>{String(p.displayValue)}{p.units ? ` ${p.units}` : ''}</dd></Fragment>)}
              </dl>
            </div>
          )}
          <div className="card">
            <dl className="kv">
              <dt>Number</dt><dd>{item.number}</dd>
              <dt>Type</dt><dd>{item.kind}</dd>
              <dt>Version</dt><dd><VersionBadge n={item.version} /></dd>
              <dt>State</dt>
              <dd style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                <StateBadge state={item.state} />
                {NEXT[item.state].length > 0 && (
                  <Select.Root size="1" value="" onValueChange={setState}>
                    <Select.Trigger placeholder="Change…" /><Select.Content>{NEXT[item.state].map((s) => <Select.Item key={s} value={s}>{s}</Select.Item>)}</Select.Content>
                  </Select.Root>
                )}
              </dd>
              <dt>Size</dt><dd>{fmtSize(item.size)}</dd>
              <dt>Created</dt><dd>{fmtDate(item.createdAt)} by {item.createdBy}</dd>
              <dt>Modified</dt><dd>{fmtDate(item.modifiedAt)} by {item.modifiedBy}</dd>
              {jobId && <><dt>Source job</dt><dd><a href={`${MAIN_APP}#/jobs/${jobId}`}>{jobId}</a></dd></>}
            </dl>
            <div style={{ display: 'flex', gap: 8, marginTop: 14, flexWrap: 'wrap' }}>
              <Button asChild><a href={fileUrl(item.id, item.version, true)} download><DownloadIcon /> Download</a></Button>
              <Button variant="soft" disabled={busy} onClick={() => input.current.click()}><UploadIcon /> Upload new version</Button>
              <input ref={input} type="file" hidden onChange={(e) => { if (e.target.files[0]) checkIn(e.target.files[0]); e.target.value = ''; }} />
            </div>
          </div>
          <div className="card">
            <b style={{ fontSize: 13 }}>Version history</b>
            <table className="grid" style={{ marginTop: 6 }}>
              <tbody>
                {versions.map((v) => (
                  <tr key={v.version}>
                    <td><VersionBadge n={v.version} /></td>
                    <td>{fmtDate(v.createdAt)}<br /><span style={{ color: 'var(--gray-10)' }}>{v.createdBy}{v.comment ? ` · ${v.comment}` : ''}</span></td>
                    <td style={{ textAlign: 'right' }}>{v.version === item.version ? <Badge variant="soft">current</Badge> : null}<br /><a href={fileUrl(item.id, v.version, true)} download>{fmtSize(v.size)}</a></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </>
  );
}
