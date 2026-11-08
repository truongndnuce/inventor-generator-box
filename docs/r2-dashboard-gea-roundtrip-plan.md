# Kế hoạch: kho tài liệu Cloudflare R2, tải DWG, 3 trang web và `.gea` hai chiều

Tài liệu này bổ sung cho `cadflow-order-to-model-plan.md` (mục 3.1, 7, 8, 15), `drawing-and-views-plan.md` (phần xuất bản vẽ) và `smartassembly-to-inventor-mapping.md` (mục 11, an toàn script).

## 1. Mục tiêu và ngoài phạm vi

**Hiện trạng**
- Artifact nằm trên đĩa server: `be\Cadflow.Api\data\artifacts\<jobId>\<name>` (`JobStore.ArtifactDir`).
- Web chỉ có 2 trang: New order (`#/`) và Orders (`#/orders`), cộng trang job `#/jobs/<id>`.
- Bản vẽ chỉ có PDF, chưa có DWG. `DrawingBuilder` đóng bản vẽ mà không lưu `.idw`.
- Endpoint `GET /api/jobs/{id}/artifacts/{name}` không kiểm tra trạng thái job, nên file có thể tải được khi job còn `running`.
- Chưa có import hay export `.gea` trên web. Worker chỉ chạy `transformer.gea` cứng trong thư mục add-in.

**Mục tiêu**
1. Dùng Cloudflare R2 làm kho lưu mặt cắt, bản vẽ và các file kết quả.
2. Cho tải bản vẽ dạng `.dwg` (xem vẫn bằng PDF).
3. Tách web thành 3 trang: **New order**, **Ordered** (job đang chạy), **Dashboard** (trạng thái các job đã chạy).
4. Job đang chạy không được mở kết quả. Rule này được thực thi ở cả giao diện và server.
5. `.gea` hai chiều: import vào thì giao diện nhận form và giá trị; export từ web ra cũng là `.gea`.

**Ngoài phạm vi**: đăng nhập và phân quyền thật, chuyển `jobs.json` sang cơ sở dữ liệu, chính sách xóa artifact cũ.

## 2. Các quyết định (đã chốt)

| # | Chủ đề | Quyết định |
|---|---|---|
| R1 | Import `.gea` | Script + giá trị (đã chốt): server compile khối FORM để sinh form động; order chạy chính script đó, `WorkItem` mang nội dung script |
| R2 | Upload lên R2 | Presigned URL (đã chốt): server cấp PUT, worker upload thẳng; tải xuống bằng presigned GET, chỉ cấp khi job `succeeded` |
| R3 | Người dùng | Tên nhập tay (đã chốt): dùng `RequestedBy`, lưu ở trình duyệt, chưa có đăng nhập |
| R4 | Dashboard | Thẻ thống kê, thời gian build, lọc và xem lại job cũ, nhân bản đơn (đã chốt) |

## 3. R2 làm kho tài liệu

### 3.1 Cách lưu

| Mục | Nội dung |
|---|---|
| Bucket | Một bucket private, không bật public access |
| Key | `jobs/<jobId>/<name>` (cùng quy tắc `safeName` hiện có) |
| Metadata | `Artifact` thêm `Key`, `Sha256`, `ContentType`. `Name`, `Kind`, `Size` giữ nguyên |
| Metadata job | `jobs.json` giữ nguyên ở giai đoạn này |
| Cấu hình | `R2:AccountId`, `R2:Bucket`, `R2:AccessKeyId`, `R2:SecretAccessKey` qua user-secrets hoặc biến môi trường, không ghi vào `appsettings.json` |
| Thư viện | AWSSDK.S3, endpoint `https://<AccountId>.r2.cloudflarestorage.com`, region `auto` |
| Dev | Khi chưa cấu hình R2, fallback ra đĩa cục bộ như hiện nay |

### 3.2 Luồng upload (worker)

```
Worker --POST /api/worker/jobs/{id}/artifacts/presign {name,kind,size,sha256}--> Server
Server --> {putUrl, key, expiresAt}
Worker --PUT putUrl (file)--> R2
Worker --POST /api/worker/jobs/{id}/artifacts/commit {name,key}--> Server
Server: HEAD R2 kiểm tra size, ghi ArtifactInfo
```

- Thay cho upload raw-body hiện tại (`Program.cs`, dòng 114-130, và `ServerClient.UploadAsync`).
- Khóa R2 chỉ nằm ở server. Worker chỉ nhận URL có hạn ngắn.
- Chỉ job `running` mới xin được presign. Server kiểm tra worker đang giữ job đó.

### 3.3 Luồng tải xuống

- `GET /api/jobs/{id}/artifacts/{name}` (`Program.cs`, dòng 76-84) đổi như sau:
  - job chưa `succeeded` thì trả `409 Conflict`;
  - job `succeeded` thì trả `302` sang presigned GET (hạn khoảng 5 phút).
- Nút tải dùng `Content-Disposition: attachment`. Xem ảnh và PDF dùng inline.
- Cần CORS trên bucket cho origin của giao diện, nếu `<embed>` PDF hay `<img>` tải trực tiếp từ R2.

## 4. Bản vẽ DWG

- `DrawingBuilder` (`Services\DrawingBuilder.cs`, quanh dòng 119, cạnh `ExportPdf` ở dòng 274-284) thêm `ExportDwg` qua translator DWG của Inventor.
- Mã translator DWG chưa có trong code (mới có PDF và STEP). Phải tra trong spike, giống cách đã làm với PDF.
- `InventorJobExecutor` upload thêm file với `kind = "dwg"`.
- `ContentTypeFor` (`Program.cs`, dòng 144) thêm `.dwg` → `image/vnd.dwg` và `.gea` → `text/plain; charset=utf-8`.
- Giao diện: bản vẽ xem bằng PDF (`<embed>`), nút **Download DWG** nằm cạnh PDF, STEP, ảnh.
- Cân nhắc lưu `.idw` gốc (đang là mục còn mở trong `drawing-and-views-plan.md`).

## 5. Ba trang web

Router hiện ở `fe\app.js` (dòng 269-282), thanh điều hướng ở `fe\index.html` (dòng 12-15).

| Trang | Route | Nội dung |
|---|---|---|
| New order | `#/`, `#/new/<code>` | Như `renderConfigurator` hiện nay, thêm nút **Import .gea** (mục 6.2) |
| Ordered | `#/orders` | Chỉ liệt kê job `queued` và `running`. Bấm vào mở trang tiến độ: timeline, bước hiện tại, nút Cancel. **Không** có gallery, PDF hay nút tải. Job xong tự rời khỏi danh sách và chuyển sang Dashboard |
| Dashboard | `#/dashboard` | Xem bên dưới |

### 5.1 Dashboard

- **Thẻ thống kê**: số job theo trạng thái (queued, running, succeeded, failed, cancelled) và tỉ lệ thành công.
- **Thời gian build**: trung bình và biểu đồ theo ngày, tính từ `StartedAt` và `FinishedAt`.
- **Bảng job** lọc theo sản phẩm, trạng thái, khoảng ngày, người đặt (`RequestedBy`). Mặc định hiện job của tên đang lưu ở trình duyệt, có công tắc "tất cả".
- **Xem lại job cũ**: bấm job `succeeded` mở trang kết quả với gallery, PDF, tải DWG, STEP, PDF, `.gea`.
- **Nhân bản đơn**: nút "Order lại" đổ inputs của job cũ vào New order (tái dùng cơ chế `sessionStorage` của "Edit and resubmit", `fe\app.js` dòng 232).

### 5.2 Trang chi tiết job `#/jobs/<id>`

- Giữ route cho liên kết cũ. `queued` và `running` chỉ hiện tiến độ. Phần Result hiện chỉ khi `succeeded` (gate đã có ở `fe\app.js` dòng 246 và 250).
- Giao diện không đủ. **Server phải chặn** (mục 3.3), để không lách bằng cách gõ URL artifact.

### 5.3 API mới

| Endpoint | Mục đích |
|---|---|
| `GET /api/jobs?status=&product=&requestedBy=&from=&to=&page=` | Danh sách có lọc và phân trang. Ordered dùng `status=queued,running` |
| `GET /api/stats?from=&to=` | Đếm theo trạng thái, thời gian build trung bình, số job theo ngày |

`store.List()` đã trả toàn bộ job mới nhất trước, nên giai đoạn đầu lọc và gom trong bộ nhớ.

## 6. `.gea` hai chiều

### 6.1 Export (web → `.gea`)

- `GET /api/jobs/{id}/export.gea` trả file `.gea` gồm script đã chạy và giá trị đầu vào của job.
- Job lưu thêm `ScriptText` và `ScriptSha256` để export đúng bản đã chạy, kể cả khi script gốc đổi sau đó.
- Cách ghi giá trị vào file theo `sa-docs\gea-language-spec.md` và tương tự cơ chế `GEA_Script` đã lưu trong `.ipt` (`Services\Gea\GeaDocumentService.cs`, dòng 53 và 113).
- Nút **Export .gea** có ở trang kết quả và Dashboard.

### 6.2 Import (`.gea` → web)

1. Người dùng chọn file `.gea` ở New order (hoặc kéo thả).
2. Giao diện gọi `POST /api/gea/import`. Server compile bằng `GeaScript.Compile` và `EvaluateForm` (`InventorAddin\Gea\GeaInterpreter.cs`, dòng 79 và 89).
3. Server trả schema form (cùng dạng với `/api/products/{code}`: `groups`, `parameters`, `defaults`) và danh sách diagnostics có dòng, cột.
4. Giao diện dựng form từ schema và điền giá trị trong file. Lỗi hiển thị ngay, không cho submit khi còn lỗi.
5. Submit gửi `script` trong `POST /api/jobs`. `WorkItem` mang `ScriptText`.
6. `InventorJobExecutor` (đang cứng `TransformerScript = "transformer.gea"`, dòng 35) chạy script nhận được thay vì đọc file theo tên sản phẩm.

Phần interpreter không phụ thuộc Inventor, nên link thư mục `Gea\` vào `Cadflow.Api` giống cách đang link `BoxParams.cs` (csproj dòng 15-16). Server nhờ đó tự kiểm tra script, không cần Inventor.

### 6.3 An toàn

`smartassembly-to-inventor-mapping.md` mục 11 đang nói người đặt hàng chỉ cung cấp giá trị. Quyết định R1 nới điều này, nên cần các lớp bảo vệ sau:

- Giới hạn kích thước file và số bước thực thi.
- `GeaChecker` chạy trước khi nhận job, chặn lệnh ngoài danh sách cho phép.
- Không chạy chương trình ngoài, không ghi ngoài thư mục job (giữ nguyên cấm ở mục 11).
- Job tạo từ script import được đánh dấu `ScriptSource = "imported"`, hiển thị rõ trên giao diện.
- Rủi ro còn lại: chưa có đăng nhập hay vai trò, nên ai truy cập được web đều import được. Cần phân quyền nếu mở rộng ra ngoài mạng nội bộ.

## 7. Giai đoạn

| Giai đoạn | Việc | Hoàn thành khi |
|---|---|---|
| P0 | Chặn artifact theo trạng thái ở server; tách 3 trang; `GET /api/jobs` có lọc | Job `running` mở URL artifact bị 409; Ordered chỉ có job đang chạy; kết quả chỉ xem được từ Dashboard |
| P1 | R2 và presign (mục 3) | Worker upload thẳng lên R2; tải job `succeeded` qua presigned GET; không còn file mới trên đĩa |
| P2 | DWG (mục 4) | Job mới có `.dwg` mở được trong AutoCAD hoặc Inventor, có nút tải |
| P3 | `/api/stats` và Dashboard (mục 5.1) | Thẻ, biểu đồ, lọc và Order lại hoạt động |
| P4 | Export `.gea` (mục 6.1) | File export import lại được, ra cùng giá trị |
| P5 | Import `.gea` và chạy script gửi lên (mục 6.2, 6.3) | Import `tank-level1.gea` ra form đúng, order chạy xong, kết quả khớp bản chạy từ add-in |

- [x] P0 (2026-11-08): server chặn artifact khi job chưa `succeeded` (409), `GET /api/jobs` có lọc, web có 3 trang. Đã kiểm tra bằng worker giả lập: artifact job `running` → 409, job xong → 200, lọc `status`/`product`/`requestedBy` đúng. Giao diện mới mới qua kiểm tra cú pháp, chưa xem trong trình duyệt.
- [x] P1 (2026-11-08): `R2Store.cs` (AWSSDK.S3 4.0.104.3, presigned PUT 15 phút / GET 5 phút), worker `POST .../artifacts/presign` rồi PUT thẳng R2 rồi `.../artifacts/commit` (server HEAD kiểm tra object rồi ghi `ArtifactInfo.Key`); tải về là 302 sang presigned GET, chỉ khi job `succeeded`, `?download=true` để ép `attachment`. Chưa cấu hình R2 hoặc server cũ thì giữ upload qua đĩa như trước (job cũ không có `Key` vẫn đọc từ đĩa). Đã test thật với bucket `sa-inventor`: job box lên R2 đủ 8 file (ipt, stp, 4 png, pdf, dwg), không có file nào trên đĩa, PDF tải về hợp lệ, URL không chữ ký bị R2 từ chối (bucket private). Cấu hình: `dotnet user-secrets set "R2:AccountId|Bucket|AccessKeyId|SecretAccessKey" ... --project be/Cadflow.Api` hoặc biến môi trường `R2__AccountId`... Retention (2026-11-08): `RetentionService` xóa file kết quả (R2 và đĩa) của job đã kết thúc quá `Retention:Days` ngày (mặc định 0 = giữ mãi), mỗi giờ quét một lần; bản ghi job, thông số, script vẫn giữ nên Dashboard và "Order again" không mất, job ghi `artifactsPurgedAt` và trang job báo file đã bị xóa. Đã test: job giả lập 3 ngày tuổi với `Retention:Days=1` bị gỡ file và ghi sự kiện (chưa kiểm riêng việc object biến mất khỏi R2, chỉ thấy lệnh xóa không báo lỗi). Chưa làm: CORS bucket (chỉ cần nếu trình duyệt `fetch` thẳng từ R2; hiện `<img>`/PDF/tải về không cần; token Object Read & Write thường cũng không đủ quyền đặt CORS, phải đặt trong dashboard Cloudflare).
- [x] P2 (2026-11-08): `DrawingBuilder.ExportDwg` dùng translator `{C24E3AC2-122E-11D5-8E91-0010B541CD80}` ("Translator: DWG", Inventor 2027.2), không cần tùy chọn. Đã chạy thật qua web → worker: job box (A4 1:2) và transformer (A3 1:20) đều có `.dwg` (header `AC1032` = AutoCAD 2018), tải được với `Content-Type: image/vnd.dwg`. Lỗi DWG chỉ là cảnh báo, không làm hỏng PDF. Chưa mở thử file DWG trong AutoCAD. Nút "Download DWG" ở web mới qua kiểm tra cú pháp.
- [x] P3 (2026-11-08): `GET /api/stats?days=&product=&requestedBy=` (đếm theo trạng thái, tỉ lệ thành công, thời gian build trung bình, theo ngày giờ máy chủ). Dashboard có 5 thẻ, 2 biểu đồ cột (job/ngày, thời gian build/ngày, có tooltip), chọn 7/14/30/90 ngày, lọc sản phẩm/trạng thái/"Only mine", nút "Order again" ở bảng job và trang kết quả. Đã chụp màn hình bằng Edge headless với 2 job thật; chưa thử bấm "Order again" và lọc bằng tay trên trình duyệt.
- [x] P4 (2026-11-08): `GET /api/jobs/{id}/export.gea` trả script đã dùng + khối giá trị `!@` ở cuối (định dạng ở `sa-docs\gea-language-spec.md` mục 19, mã ở `be\Cadflow.Api\GeaExport.cs`). Server giữ script theo SHA-256 trong `data\scripts\`; `Job` có `ScriptName`, `ScriptSha256`. Web có nút "Export .gea" ở trang job và Dashboard. Đã kiểm: file export compile sạch bằng `GeaScript.Compile`, đọc lại đủ 20 giá trị, export lại không nhân đôi khối, job transformer thật vẫn build xong. Sản phẩm `box` lúc đầu chưa có script nên chưa export được; đã giải quyết bằng `Scripts\box.gea` (xem bước "box.gea" bên dưới). Chưa mở file export trong add-in Inventor.
- [x] P5 (2026-11-08): import `.gea` và đặt hàng từ script. API: `POST /api/gea/import` (biên dịch, lưu theo SHA-256, trả form + giá trị đọc từ khối `!@`), `POST /api/gea/{id}/form` (tính lại FORM mỗi khi giá trị đổi: ô ẩn/hiện, `ASSERT`), `POST /api/jobs` nhận `scriptId` + `inputs` + `textInputs` (job có `productCode = "imported"`, `scriptSource = "imported"`). Server link `InventorAddin\Gea\*.cs` nên tự biên dịch script; `WorkItem` mang `ScriptText`, worker chạy đúng script đó. Hỗ trợ ô số, ô tích, ô radio và ô text (`!@TEXT`). Web: nút "Import .gea" ở New order, trang `#/import/<scriptId>` dựng form động, "Order again"/"Edit and resubmit" mở lại đúng script. Đã chạy thật trên Inventor 2027.2: import file export của transformer (build xong, có PDF/DWG), import `tank-level1.gea` (có radio `TR_Paint`) → build xong → export → import lại ra cùng `scriptId` và giá trị; transformer có sẵn vẫn build bình thường qua `ScriptText`. Server từ chối script lỗi cú pháp, quá 256 KB, rỗng, giá trị vi phạm `MIN/MAX/ASSERT`, lựa chọn radio sai, `scriptId` lạ; vòng `FOR` lồng nhau bị chặn bởi giới hạn 1.000.000 lệnh (~1,2 giây). Hạn chế: bản vẽ của job import không có chuỗi kích thước chiều cao trên mặt cắt (chỉ biết mức cao của box/transformer có sẵn), hướng đứng giả định Y-up như transformer; chưa có đăng nhập nên ai vào được web đều import được.

**Bước bổ sung: `box.gea` (2026-11-08).** `InventorAddin\Scripts\box.gea` mô tả box bằng builder `Tank` + `Cover` (cùng thông số `BX_*`, mặc định, MIN/MAX và 7 luật `ASSERT` như `BoxParams.Validate`). `Products.cs` gắn `ScriptFile: "box.gea"`, server link file này như `transformer.gea`; worker dựng box từ `ScriptText` server gửi xuống, còn server cũ không gửi script thì dùng lại cổng C# (`InventorBoxGateway`). Đã kiểm: (1) so luật FORM của script với `BoxParams.Validate` trên 120.000 bộ giá trị ngẫu nhiên và đúng biên (khoảng 36.600 bộ hợp lệ): không lệch bộ nào; (2) build thật trên Inventor 2027.2: mặc định ra 68,559 cm³, 2 body, 0,0686 kg, A4 1:2, 5 view, 6 kích thước, giống hệt bản C# cũ; bộ giá trị lớn (220×140×90, thành 3, bo góc 8...) ra 621,493 cm³; cả hai khớp công thức giải tích đến 3 chữ số thập phân; (3) export job box rồi import lại, đặt hàng từ file import ra cùng 621,493 cm³, và giá trị vi phạm luật bị từ chối. Khác biệt cần biết: model box từ script có trục đứng là **Y** (builder dựng theo Y-up như máy biến áp), bản C# cũ là Z; thân và nắp tên `BX_Box`/`BX_Lid`; tham số `BX_*` trong `.ipt` không còn là User Parameter điều khiển hình (builder tạo hình trực tiếp từ giá trị), nên sửa tham số trong Inventor không còn đổi hình như bản C#. Panel box tương tác trong add-in vẫn dùng bản C# parametric.

## 8. Rủi ro

| Rủi ro | Cách giảm |
|---|---|
| Lộ khóa R2 | Khóa chỉ ở server, qua user-secrets hoặc biến môi trường; worker chỉ có URL ngắn hạn; bucket private |
| Presigned PUT hết hạn khi file lớn | Hạn 15 phút cho PUT; xin lại presign khi lỗi 403 |
| Translator DWG lệch phiên bản Inventor | Tra mã trong spike; ghi phiên bản DWG đã thử; lỗi DWG không làm fail cả job (cảnh báo trong timeline) |
| Script độc hại hoặc quá nặng | Mục 6.3: checker, giới hạn kích thước và số bước, thư mục job cô lập, timeout |
| Job cũ có artifact trên đĩa, chưa có `Key` | Serve fallback đĩa nếu `Key` rỗng; script chuyển dần lên R2 nếu cần |
| `jobs.json` ghi lại cả danh sách mỗi lần đổi, chậm khi nhiều job | Chấp nhận ở giai đoạn này; chuyển DB nằm ngoài phạm vi, ghi lại để làm sau |
| Tên người dùng nhập tay có thể giả mạo | Chấp nhận (R3); chỉ dùng để lọc, không dùng để phân quyền |

## 9. Việc làm ngay tiếp theo

1. Làm P0: kiểm tra trạng thái ở `GET /api/jobs/{id}/artifacts/{name}`, thêm lọc cho `GET /api/jobs`, dựng route `#/dashboard` và đổi `#/orders` chỉ còn job đang chạy.
2. Tạo bucket R2 và API token (quyền Object Read & Write giới hạn theo bucket), cấu hình CORS.
3. Spike translator DWG trên Inventor 2027 với bản vẽ transformer và box, ghi mã translator vào `drawing-and-views-plan.md`.
4. Quyết định định dạng ghi giá trị vào `.gea` khi export, đối chiếu với `sa-docs\gea-language-spec.md`.
