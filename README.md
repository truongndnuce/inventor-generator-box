# inventor-generator-box

Web đặt hàng → hàng đợi → add-in trong Inventor dựng model → trả hồ sơ. Kế hoạch ở `docs/` (bước **W0 + một phần W2** của mục 15.7 trong `cadflow-order-to-model-plan.md`).

```
fe/  (web tĩnh)  ──►  be/Cadflow.Api (ASP.NET Core, hàng đợi job)  ◄──  InventorAddin (nhận job, dựng hộp, tải kết quả lên)
```

| Thư mục | Vai trò |
|---------|---------|
| `fe/` | Configurator (form sinh từ schema, kiểm tra luật qua API), danh sách đơn, trang job theo dõi thời gian thực. React + Radix UI Themes (Vite); cần `npm run build` để tạo `fe/dist`. |
| `be/Cadflow.Api/` | API: sản phẩm, đặt đơn, job, tải hồ sơ, API cho worker. Dùng chung file `BoxParams.cs` với add-in nên luật chỉ có một bản. Phục vụ luôn `fe/`. |
| `InventorAddin/` | Add-in Inventor 2027. Ngoài UI chỉnh tay, thư mục `Web/` là **web worker**: hỏi server nhận job, dựng trên UI thread của Inventor, xuất `.ipt`, `.stp`, `.png`. |

## Chạy

1. **Server + web** (cần .NET 10 SDK và Node.js 20+; web là React + Radix UI Themes, build bằng Vite):
   ```
   cd fe && npm install && npm run build      # tạo fe/dist, server phục vụ thư mục này
   cd .. && dotnet run --project be/Cadflow.Api
   ```
   Sửa giao diện: `cd fe && npm run dev` (http://localhost:5173, tự chuyển tiếp `/api` sang server :18080).
   Mở http://localhost:18080. Dữ liệu job nằm ở `be/Cadflow.Api/data/` (đổi bằng cấu hình `DataDir`).
2. **Add-in**: build `InventorAddin/InventorAddin.sln` (tự ghi file `.addin` vào `%AppData%\Autodesk\Inventor 2027\Addins`), mở Inventor 2027. Khi nạp, add-in tự kết nối server. Tiêu đề cửa sổ Box Generator hiện `web: connected`.
3. Đặt đơn trên web: job chuyển `queued → running → succeeded`, trang job có ảnh, STEP, `.ipt`.

## Script `.gea` (kiểu SmartAssembly)

Đặc tả ngôn ngữ ở `sa-docs/gea-language-spec.md`. Trình thông dịch nằm ở `InventorAddin/InventorAddin/Gea/` (lexer, parser, kiểm tra ngữ nghĩa, FORM, BUILD; không phụ thuộc Inventor), Inventor Host và builder ở `Services/Gea/`. Script mẫu: `InventorAddin/InventorAddin/Scripts/` (`tank-level1.gea`, `transformer.gea`, `transformer-hv.gea`: máy biến áp ngoài trời chi tiết với cánh tản nhiệt dạng lá, bình dầu có giá đỡ, ống dầu, hai hàng sứ HV/LV, van xả, biển cảnh báo).

Dùng trong add-in, tab **Script (.gea)**:
1. **Open…** một file `.gea`, hoặc chọn **Template** rồi **Open template** (mở bản sao sửa được trong `%AppData%\InventorBoxAddin\scripts`).
2. Tab **Form** sinh từ khối `BEGIN_FORM`: ô ẩn/hiện theo `IF`, lỗi `MIN`/`MAX`/`ASSERT` tô đỏ đúng ô.
3. Sửa script ngay ở tab **Script** (lỗi có dòng, cột; nhấp đúp để nhảy tới dòng) hoặc **Edit externally**: lưu file là add-in tự nạp lại; bật *Apply automatically when the script file is saved* để model tự cập nhật.
4. **Apply**: lưu script, rồi trong **một Transaction** xóa mọi body/feature/sketch/work plane do script tạo lần trước, đồng bộ thông số thành User Parameter, chạy lại `BEGIN_BUILD`. Lỗi ở bất kỳ bước nào thì model giữ nguyên; Ctrl+Z hoàn tác cả lần Apply. Hình học người dùng tự vẽ (không mang dấu `GEA_Owner`) được giữ.
5. Script và giá trị được lưu trong Part (AttributeSet `GEA_Script`): mở lại `.ipt` là add-in nạp lại script để sửa tiếp (**Load from model**).

Builder có sẵn cho `CREATE_FEATURE builder:<Tên>`: `Tank`, `Cover`, `Block`, `Cylinder`, `Bushing`, `Radiator` (manifest ở `Gea/GeaManifests.cs`). Web worker dựng máy biến áp bằng `Scripts/transformer.gea` và hộp bằng `Scripts/box.gea`, nên file `.ipt` trả về cũng mở và sửa được bằng tab Script. Trên web, mỗi job dựng bằng script có nút **Export .gea** (script + giá trị đã đặt) và **Import .gea** ở trang New order dựng lại form từ file đó (xem `docs/r2-dashboard-gea-roundtrip-plan.md` và `sa-docs/gea-language-spec.md` mục 19).

## Cấu hình

- **Add-in**: `%AppData%\InventorBoxAddin\worker.json` (tự tạo lần đầu): `enabled`, `serverUrl`, `workerKey`, `workerName`, `pollSeconds`. Log ở `%AppData%\InventorBoxAddin\logs`.
- **R2 (tùy chọn)**: `dotnet user-secrets set "R2:AccountId" ... --project be/Cadflow.Api` cho `R2:AccountId`, `R2:Bucket`, `R2:AccessKeyId`, `R2:SecretAccessKey` (hoặc biến môi trường `R2__AccountId`...; thêm `R2:Endpoint` nếu dùng jurisdiction EU). Có đủ thì worker tải kết quả thẳng lên bucket R2 (private) và web tải qua URL ký sẵn; không có thì lưu ở `data/artifacts` như cũ. Log khởi động ghi chế độ đang dùng.
- **Lưu giữ file**: `Retention:Days` (mặc định 0 = giữ mãi) xóa file kết quả của job đã xong quá số ngày đó; thông tin job vẫn còn.
- **Server**: `be/Cadflow.Api/appsettings.json`: `Urls`, `Worker:Key`. **Đổi `Worker:Key` mặc định `dev-worker-key` trước khi mở ra ngoài localhost.**
- Muốn Inventor ở máy khác: đặt `Urls` thành `http://0.0.0.0:18080` và `serverUrl` của add-in trỏ tới máy chủ (nên đặt sau VPN/mạng riêng, xem mục 10 của kế hoạch).

## API worker (add-in gọi, header `X-Worker-Key`)

`POST /api/worker/heartbeat` · `POST /api/worker/claim` · `POST /api/worker/jobs/{id}/progress|artifacts|complete|fail?worker=...`

Job quá 90 giây không có nhịp tim thì tự trả về hàng đợi; tối đa 3 lần thử.
