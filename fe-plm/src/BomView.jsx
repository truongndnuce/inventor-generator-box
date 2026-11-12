import { useEffect, useMemo, useState } from 'react';
import { SegmentedControl, Spinner } from '@radix-ui/themes';
import { fileUrl, parseCsv } from './lib.js';

// The columns the worker always writes (InventorAddin.Core/Models/BomWriter.cs), with a label and whether the cells are numbers.
// Any other column is a job input (the same value on every row) and is listed separately under "Input parameters".
const COLUMNS = {
  Item: { label: 'Item', num: true },
  PartNumber: { label: 'Part number' },
  Description: { label: 'Description' },
  FileName: { label: 'File' },
  Material: { label: 'Material' },
  Quantity: { label: 'Qty', num: true },
  MassKg: { label: 'Mass (kg)', num: true },
  VolumeCm3: { label: 'Volume (cm³)', num: true },
  SizeX_mm: { label: 'Size X (mm)', num: true },
  SizeY_mm: { label: 'Size Y (mm)', num: true },
  SizeZ_mm: { label: 'Size Z (mm)', num: true },
};
const MAX_ROWS = 1000;

/** The writer prefixes a cell that starts with = + @ with an apostrophe so Excel does not run it as a formula; undo that for display. */
const clean = (s) => (/^'[=+@]/.test(s) ? s.slice(1) : s);

/** Shows a BOM CSV as a table (or the raw text), plus the job input parameters. */
export default function BomView({ item }) {
  const [text, setText] = useState(null);
  const [error, setError] = useState('');
  const [mode, setMode] = useState('table');

  useEffect(() => {
    let alive = true;
    setText(null); setError('');
    fetch(fileUrl(item.id, item.version))
      .then((r) => (r.ok ? r.text() : Promise.reject(new Error(`${r.status} ${r.statusText}`))))
      .then((t) => alive && setText(t))
      .catch((e) => alive && setError(e.message));
    return () => { alive = false; };
  }, [item.id, item.version]);

  const parsed = useMemo(() => {
    if (text == null) return null;
    const rows = parseCsv(text);
    if (rows.length === 0) return { header: [], body: [], shown: [], inputs: [] };
    const [header, ...body] = rows;
    const shown = header.map((h, i) => ({ h, i })).filter((c) => COLUMNS[c.h]);
    const inputs = header.map((h, i) => ({ h, i })).filter((c) => !COLUMNS[c.h]).map((c) => ({ name: c.h, value: clean(body[0]?.[c.i] ?? '') }));
    return { header, body, shown, inputs };
  }, [text]);

  if (error) return <div className="bom-msg">Could not load the BOM ({error}). Download it to open in Excel.</div>;
  if (!parsed) return <div className="bom-msg"><Spinner /> Loading…</div>;
  if (parsed.header.length === 0) return <div className="bom-msg">This BOM file is empty.</div>;

  return (
    <div className="bom">
      <div className="bom-bar">
        <b>Bill of materials</b>
        <span className="bom-count">{parsed.body.length} {parsed.body.length === 1 ? 'line' : 'lines'}</span>
        <span style={{ flex: 1 }} />
        <SegmentedControl.Root size="1" value={mode} onValueChange={setMode}>
          <SegmentedControl.Item value="table">Table</SegmentedControl.Item>
          <SegmentedControl.Item value="raw">Raw CSV</SegmentedControl.Item>
        </SegmentedControl.Root>
      </div>

      {mode === 'raw' ? (
        <pre className="bom-raw">{text.replace(/^﻿/, '')}</pre>
      ) : (
        <>
          <div className="bom-scroll">
            <table className="grid bom-table">
              <thead>
                <tr>{parsed.shown.map((c) => <th key={c.h} className={COLUMNS[c.h].num ? 'num' : ''}>{COLUMNS[c.h].label}</th>)}</tr>
              </thead>
              <tbody>
                {parsed.body.slice(0, MAX_ROWS).map((r, ri) => (
                  <tr key={ri}>{parsed.shown.map((c) => <td key={c.h} className={COLUMNS[c.h].num ? 'num' : ''}>{clean(r[c.i] ?? '') || <span className="bom-empty">–</span>}</td>)}</tr>
                ))}
                {parsed.body.length === 0 && <tr><td colSpan={Math.max(1, parsed.shown.length)} className="bom-empty" style={{ textAlign: 'center', padding: 24 }}>No lines.</td></tr>}
              </tbody>
            </table>
            {parsed.body.length > MAX_ROWS && <div className="bom-empty" style={{ padding: 8 }}>Showing the first {MAX_ROWS} of {parsed.body.length} lines. Download the file for all of them.</div>}
          </div>

          {parsed.inputs.length > 0 && (
            <div className="bom-inputs">
              <b>Input parameters</b>
              <dl className="bom-kv">
                {parsed.inputs.map((p) => <div key={p.name} className="bom-kv-row"><dt>{p.name}</dt><dd>{p.value || '–'}</dd></div>)}
              </dl>
            </div>
          )}
        </>
      )}
    </div>
  );
}
