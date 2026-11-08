import { useEffect, useState } from 'react';
import { Card, Checkbox, Flex, Heading, Select, Table, Text } from '@radix-ui/themes';
import { api, fmtDuration, usePolling } from '../lib.js';
import { ExportGeaLink, OrderAgainButton } from '../components.jsx';
import { JOB_COLUMNS, JobCells } from './Orders.jsx';

const STATUSES = ['queued', 'running', 'succeeded', 'failed', 'cancelled'];
const DAYS = [[7, 'Last 7 days'], [14, 'Last 14 days'], [30, 'Last 30 days'], [90, 'Last 90 days']];
const ALL = 'all'; // Radix Select không nhận giá trị rỗng

/** Biểu đồ cột một chuỗi: points = [{ label, value, tip }]. Cột bo góc ở đầu, hover hiện tooltip. */
function BarChart({ title, points, integer = false, unit = '' }) {
  const [hot, setHot] = useState(null); // { i, x, y }
  const W = 460, H = 170, L = 36, R = 8, T = 10, B = 24;
  const max = Math.max(...points.map((p) => p.value ?? 0), 0);
  const top = max === 0 ? 1 : integer ? Math.max(1, Math.ceil(max)) : Math.ceil(max / 10) * 10 || 10;
  const slot = (W - L - R) / points.length;
  const bw = Math.max(3, Math.min(28, slot * 0.6));
  const y = (v) => T + (H - T - B) * (1 - v / top);
  const labelEvery = Math.ceil(points.length / 5);

  const onMove = (i) => (e) => {
    const r = e.currentTarget.ownerSVGElement.parentElement.getBoundingClientRect();
    setHot({ i, x: e.clientX - r.left + 12, y: Math.max(e.clientY - r.top - 34, 0) });
  };

  return (
    <Card size="2">
      <Heading size="3" mb="3">{title}{unit && <Text color="gray" weight="regular">{` (${unit})`}</Text>}</Heading>
      <div className="chart-wrap">
        <svg viewBox={`0 0 ${W} ${H}`} className="chart" role="img" aria-label={title}>
          {[0, top].map((v) => (
            <g key={v}>
              <line x1={L} x2={W - R} y1={y(v)} y2={y(v)} className={v === 0 ? 'axis' : 'grid'} />
              <text x={L - 6} y={y(v) + 4} textAnchor="end" className="tick">{v}</text>
            </g>
          ))}
          {points.map((p, i) => {
            const x = L + slot * i + (slot - bw) / 2;
            const v = p.value ?? 0;
            const y0 = y(0), y1 = y(v), r = Math.min(3, (y0 - y1) / 2, bw / 2);
            return (
              <g key={i}>
                {v > 0 && (
                  <path className={`bar${hot?.i === i ? ' hot' : ''}`}
                    d={`M${x},${y0} V${y1 + r} Q${x},${y1} ${x + r},${y1} H${x + bw - r} Q${x + bw},${y1} ${x + bw},${y1 + r} V${y0} Z`} />
                )}
                {(points.length - 1 - i) % labelEvery === 0 && (
                  <text x={x + bw / 2} y={H - 6} textAnchor="middle" className="tick">{p.label}</text>
                )}
                {/* Vùng bắt hover rộng hơn cột (cả khe và cột rỗng). */}
                <rect x={L + slot * i} y={T} width={slot} height={H - T - B} className="hit"
                  onMouseEnter={onMove(i)} onMouseMove={onMove(i)} onMouseLeave={() => setHot(null)} />
              </g>
            );
          })}
        </svg>
        {hot && <div className="chart-tip" style={{ left: hot.x, top: hot.y }}>{points[hot.i].tip}</div>}
        {max === 0 && <div className="chart-empty"><Text color="gray">No data in this period</Text></div>}
      </div>
    </Card>
  );
}

function Tile({ label, value, sub, color }) {
  return (
    <Card size="2">
      <Text as="div" size="1" color="gray" style={{ textTransform: 'uppercase', letterSpacing: '0.04em' }}>{label}</Text>
      <div className="tile-value" style={color ? { color: `var(--${color}-10)` } : undefined}>{value}</div>
      {sub && <Text as="div" size="1" color="gray">{sub}</Text>}
    </Card>
  );
}

/** Dashboard: thẻ thống kê, biểu đồ theo ngày, và toàn bộ job đã chạy (có lọc). Job 'succeeded' mở ra được trang kết quả. */
export default function Dashboard() {
  const [days, setDays] = useState('14');
  const [status, setStatus] = useState(ALL);
  const [product, setProduct] = useState(ALL);
  const [mine, setMine] = useState(false);
  const [products, setProducts] = useState([]);
  const [stats, setStats] = useState(null);
  const [jobs, setJobs] = useState(null);
  const user = localStorage.getItem('cadflow.user') || '';

  useEffect(() => { api('/api/products').then(setProducts).catch(() => {}); }, []); // lọc theo sản phẩm là phụ

  usePolling(async () => {
    const sq = new URLSearchParams({ days });
    const jq = new URLSearchParams();
    if (product !== ALL) { sq.set('product', product); jq.set('product', product); }
    if (mine && user) { sq.set('requestedBy', user); jq.set('requestedBy', user); }
    if (status !== ALL) jq.set('status', status);
    const [s, j] = await Promise.all([api(`/api/stats?${sq}`), api('/api/jobs' + (jq.toString() ? `?${jq}` : ''))]);
    setStats(s); setJobs(j);
  }, 5000, [days, status, product, mine]);

  const shortDate = (d) => d.slice(5);
  const COLUMNS = [...JOB_COLUMNS, ''];

  return (
    <Flex direction="column" gap="4">
      <Card size="3">
        <Heading size="5" mb="3">Dashboard</Heading>
        <Flex gap="3" align="center" wrap="wrap">
          <Select.Root value={days} onValueChange={setDays}>
            <Select.Trigger aria-label="Period" />
            <Select.Content>{DAYS.map(([v, t]) => <Select.Item key={v} value={String(v)}>{t}</Select.Item>)}</Select.Content>
          </Select.Root>
          <Select.Root value={product} onValueChange={setProduct}>
            <Select.Trigger aria-label="Product" />
            <Select.Content>
              <Select.Item value={ALL}>All products</Select.Item>
              <Select.Item value="imported">Imported scripts</Select.Item>
              {products.map((p) => <Select.Item key={p.code} value={p.code}>{p.name}</Select.Item>)}
            </Select.Content>
          </Select.Root>
          <Select.Root value={status} onValueChange={setStatus}>
            <Select.Trigger aria-label="Status" />
            <Select.Content>
              <Select.Item value={ALL}>All statuses</Select.Item>
              {STATUSES.map((s) => <Select.Item key={s} value={s}>{s}</Select.Item>)}
            </Select.Content>
          </Select.Root>
          <Text as="label" size="2">
            <Flex gap="2" align="center">
              <Checkbox checked={mine} disabled={!user} onCheckedChange={(c) => setMine(c === true)} />
              {user ? `Only mine (${user})` : 'Only mine (set "Requested by" in New order first)'}
            </Flex>
          </Text>
          {jobs && <Text size="2" color="gray">{`${jobs.length} job${jobs.length === 1 ? '' : 's'} listed`}</Text>}
        </Flex>
      </Card>

      {stats && (
        <>
          <div className="tiles">
            <Tile label="Jobs" value={stats.total} sub={`last ${stats.days} days`} />
            <Tile label="Succeeded" value={stats.byStatus.succeeded} color="green" />
            <Tile label="Failed" value={stats.byStatus.failed} sub={`${stats.byStatus.cancelled} cancelled`} color={stats.byStatus.failed ? 'red' : undefined} />
            <Tile label="Success rate" value={stats.successRate == null ? '-' : `${stats.successRate}%`} sub="succeeded / (succeeded + failed)" />
            <Tile label="Avg build time" value={fmtDuration(stats.avgBuildSeconds)} sub="succeeded jobs" />
          </div>
          <div className="charts">
            <BarChart title="Jobs per day" integer
              points={stats.byDay.map((d) => ({ label: shortDate(d.date), value: d.total, tip: `${d.date}: ${d.total} job${d.total === 1 ? '' : 's'} (${d.succeeded} succeeded, ${d.failed} failed)` }))} />
            <BarChart title="Average build time" unit="seconds"
              points={stats.byDay.map((d) => ({ label: shortDate(d.date), value: d.avgBuildSeconds, tip: `${d.date}: ${d.avgBuildSeconds == null ? 'no finished builds' : fmtDuration(d.avgBuildSeconds)}` }))} />
          </div>
        </>
      )}

      <Card size="3">
        <Heading size="3" mb="3">Jobs</Heading>
        <div className="table-scroll">
          <Table.Root variant="ghost">
            <Table.Header>
              <Table.Row>{COLUMNS.map((t, i) => <Table.ColumnHeaderCell key={i}>{t}</Table.ColumnHeaderCell>)}</Table.Row>
            </Table.Header>
            <Table.Body>
              {jobs?.length === 0 && <Table.Row><Table.Cell colSpan={COLUMNS.length}><Text color="gray">No jobs match.</Text></Table.Cell></Table.Row>}
              {jobs?.map((j) => (
                <Table.Row key={j.id}>
                  <JobCells j={j} />
                  <Table.Cell>
                    <Flex gap="2"><OrderAgainButton job={j} /><ExportGeaLink job={j} /></Flex>
                  </Table.Cell>
                </Table.Row>
              ))}
            </Table.Body>
          </Table.Root>
        </div>
      </Card>
    </Flex>
  );
}
