import { useRef } from 'react';
import { Badge, Button, Callout, Flex, Text } from '@radix-ui/themes';
import { API, STATUS_COLOR, go, postJson, reorder } from './lib.js';

export function StatusBadge({ status }) {
  return <Badge color={STATUS_COLOR[status] || 'gray'} variant="soft" radius="full">{status}</Badge>;
}

/** Hộp thông báo lỗi/cảnh báo; children null thì không hiện. */
export function Notice({ color = 'red', children }) {
  if (!children || (Array.isArray(children) && children.length === 0)) return null;
  return (
    <Callout.Root color={color} size="1" mb="3" role="alert">
      <Callout.Text as="div">{children}</Callout.Text>
    </Callout.Root>
  );
}

/** Liên kết tải file .gea (script đã dùng + giá trị) của job; không hiện nếu job không dựng bằng script. */
export function ExportGeaLink({ job }) {
  if (!job.scriptSha256) return null;
  return (
    <Button asChild variant="soft" size="1" title="Download the .gea script with this job's values">
      <a href={`${API}/api/jobs/${encodeURIComponent(job.id)}/export.gea`} download={`${job.id}.gea`}>Export .gea</a>
    </Button>
  );
}

/** "Order again": đổ đầu vào của job cũ vào form New order (dùng chung cơ chế với "Edit and resubmit"). */
export function OrderAgainButton({ job, size = '1' }) {
  return <Button variant="soft" size={size} title="Open New order with this job's inputs" onClick={() => reorder(job)}>Order again</Button>;
}

/** Nút "Import .gea": gửi file lên server để biên dịch; thành công thì chuyển sang trang đặt hàng từ script. */
export function ImportGeaButton({ onError }) {
  const input = useRef(null);
  const onChange = async () => {
    const file = input.current.files[0];
    input.current.value = '';
    if (!file) return;
    onError(null);
    try {
      const text = await file.text();
      const res = await postJson('/api/gea/import', { text });
      sessionStorage.setItem('cadflow.import', JSON.stringify({
        scriptId: res.scriptId, values: res.values, texts: res.texts, warnings: res.warnings, hasValues: res.hasValues, fileName: file.name,
      }));
      go(`#/import/${res.scriptId}`);
    } catch (e) {
      const diags = (e.body?.diagnostics || []).filter((d) => d.severity === 'error').slice(0, 5);
      onError(
        <>
          <div>{`Could not import ${file.name}: ${e.message}`}</div>
          {diags.map((d, i) => <div key={i}><Text size="1" color="gray">{`line ${d.line}, col ${d.column}: ${d.code} ${d.message}`}</Text></div>)}
        </>,
      );
    }
  };
  return (
    <Flex ml="auto">
      <input ref={input} type="file" accept=".gea,text/plain" hidden onChange={onChange} />
      <Button variant="outline" title="Order from a .gea script (for example one exported from CAD Automation)" onClick={() => input.current.click()}>
        Import .gea
      </Button>
    </Flex>
  );
}
