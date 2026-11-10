import { useState } from 'react';
import { Card, Heading, Link, Table, Text } from '@radix-ui/themes';
import { api, fmtDate, productLabel, usePolling } from '../lib.js';
import { StatusBadge } from '../components.jsx';

export const JOB_COLUMNS = ['Job', 'Product', 'Status', 'Created', 'Requested by', 'Worker'];

/** The cells shared by a job row (used in Ordered and Dashboard). */
export function JobCells({ j }) {
  return (
    <>
      <Table.RowHeaderCell><Link href={`#/jobs/${j.id}`}>{j.id}</Link></Table.RowHeaderCell>
      <Table.Cell>{productLabel(j)}</Table.Cell>
      <Table.Cell><StatusBadge status={j.status} />{j.step && j.status === 'running' ? ` ${j.step}` : ''}</Table.Cell>
      <Table.Cell>{fmtDate(j.createdAt)}</Table.Cell>
      <Table.Cell>{j.requestedBy}</Table.Cell>
      <Table.Cell>{j.worker || '-'}</Table.Cell>
    </>
  );
}

/** Ordered: only waiting/running jobs. Clicking shows progress only; a finished job leaves the list by itself (see it in the Dashboard). */
export default function Orders() {
  const [jobs, setJobs] = useState(null);
  usePolling(async () => setJobs(await api('/api/jobs?status=queued,running')), 3000);

  return (
    <Card size="3">
      <Heading size="5">Ordered</Heading>
      <Text as="p" size="2" color="gray" mt="1" mb="4">
        Jobs that are waiting or being built. Finished jobs and their drawings are in the <Link href="#/dashboard">Dashboard</Link>.
      </Text>
      <div className="table-scroll">
        <Table.Root variant="ghost">
          <Table.Header>
            <Table.Row>{JOB_COLUMNS.map((t) => <Table.ColumnHeaderCell key={t}>{t}</Table.ColumnHeaderCell>)}</Table.Row>
          </Table.Header>
          <Table.Body>
            {jobs === null && <Table.Row><Table.Cell colSpan={JOB_COLUMNS.length}><Text color="gray">Loading...</Text></Table.Cell></Table.Row>}
            {jobs?.length === 0 && (
              <Table.Row><Table.Cell colSpan={JOB_COLUMNS.length}><Text color="gray">No jobs are running right now.</Text></Table.Cell></Table.Row>
            )}
            {jobs?.map((j) => <Table.Row key={j.id}><JobCells j={j} /></Table.Row>)}
          </Table.Body>
        </Table.Root>
      </div>
    </Card>
  );
}
