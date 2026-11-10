# inventor-generator-box

Web đặt hàng → hàng đợi → add-in trong Inventor dựng model → trả hồ sơ. Kế hoạch ở `docs/` (bước **W0 + một phần W2** của mục 15.7 trong `cadflow-order-to-model-plan.md`).

```
fe/  (web tĩnh)  ──►  be/Cadflow.Api (ASP.NET Core, hàng đợi job)  ◄──  các add-in Inventor (nhận job, dựng model, tải kết quả lên)
```

| Thư mục | Vai trò |
|---------|---------|
| `fe/` | Configurator (form sinh từ schema, kiểm tra luật qua API), danh sách đơn, trang job theo dõi thời gian thực. React + Radix UI Themes (Vite); cần `npm run build` để tạo `fe/dist`. |
| `be/Cadflow.Api/` | API: sản phẩm, đặt đơn, job, tải hồ sơ, API cho worker. Dùng chung các file `*Params.cs` (Models) với add-in nên luật chỉ có một bản. Phục vụ luôn `fe/`. |
| `InventorAddin/` | Bộ add-in Inventor 2027, **mỗi sản phẩm một add-in** nhưng chung một tab **Cadflow** trên ribbon của Part (xem mục *Bộ add-in* bên dưới). `InventorAddin.Core/` là thư viện dùng chung, trong đó `Web/` là **web worker**: hỏi server nhận job, dựng trên UI thread của Inventor, xuất `.ipt`, `.stp`, `.png`, `.pdf`. |

## Chạy

1. **Server + web** (cần .NET 10 SDK, Node.js 20+ và **PostgreSQL 15+**; web là React + Radix UI Themes, build bằng Vite):
   ```
   createdb cadflow                           # lần đầu; bảng tự tạo khi server khởi động (xem docs/database.md)
   cd fe && npm install && npm run build      # tạo fe/dist, server phục vụ thư mục này
   cd .. && dotnet run --project be/Cadflow.Api
   ```
   Sửa giao diện: `cd fe && npm run dev` (http://localhost:5173, tự chuyển tiếp `/api` sang server :18080).
   Mở http://localhost:18080. Job nằm trong PostgreSQL (`ConnectionStrings:Cadflow`); file kết quả, script và cấu hình sản phẩm nằm ở `be/Cadflow.Api/data/` (đổi bằng cấu hình `DataDir`). Test: `dotnet test be/Cadflow.Api.Tests` (cần PostgreSQL, xem `docs/database.md`).
2. **Add-in**: build `InventorAddin/InventorAddin.sln` (mỗi add-in tự ghi file `.addin` của mình vào `%AppData%\Autodesk\Inventor 2027\Addins`), mở Inventor 2027. Khi nạp, mỗi add-in tự kết nối server và chỉ nhận job của sản phẩm mình. Tiêu đề cửa sổ add-in hiện `web: connected`.
3. Đặt đơn trên web: job chuyển `queued → running → succeeded`, trang job có ảnh, STEP, `.ipt`.

## Bộ add-in

Mỗi add-in là một DLL, một file `.addin` và một GUID riêng nên bật/tắt độc lập trong Inventor (Add-In Manager), nhưng cùng thêm một panel vào tab **Cadflow** (`CF_Tab`) trên ribbon của Part; add-in nào nạp trước thì tạo tab.

| Add-in (`InventorAddin/Addins/`) | Panel | Cửa sổ | Job web nhận (`productCode`) |
|---|---|---|---|
| `Box` | Box Generator | Hộp có nắp: preset, xem trước, Apply | `box` |
| `Transformer` | Transformer | Script `.gea` (máy biến áp) | `transformer`, `imported` (script import từ web) |
| `GasTurbine` | Gas Turbine | Script `.gea` (turbine khí) | `gas-turbine` |
| `Generator` | Water-cooled Generator | Script `.gea` (máy phát điện làm mát bằng nước) | `generator` |

- Mỗi add-in có web worker riêng; khi lấy job nó gửi danh sách `products` cho `POST /api/worker/claim`, server chỉ giao job thuộc các mã đó (không gửi `products` = nhận tất cả, như worker cũ). Mỗi add-in dựng một job một lúc.
- Lớp nền `CadflowAddIn` (Core/Host) lo ribbon dùng chung, cửa sổ, worker, làm mới khi đổi document; `ScriptProductAddIn` dựng sẵn cửa sổ Script cho sản phẩm dựng bằng `.gea`.
- **Thêm sản phẩm mới**: (1) `Models/XxxParams.cs` + `Scripts/xxx.gea`; (2) thêm một dòng ở `Core/Web/ScriptedProducts.cs` và ở `be/Cadflow.Api/Products.cs` (+ link file vào `Cadflow.Api.csproj`); (3) thêm thư mục `Addins/Xxx` với một lớp kế thừa `ScriptProductAddIn` (copy `Addins/Generator`, đổi GUID/tên) và thêm vào solution.
- **Component dùng lại**: `INCLUDE lib:<tên>` trong `BEGIN_FORM` và `BEGIN_BUILD` chèn nhóm tham số và các bước dựng của `InventorAddin.Core/Scripts/lib/<tên>.gea` (hiện có `tank-base-cover`, đế/thùng/nắp của máy biến áp, `transformer.gea` đang dùng nó). Thêm thư viện: đặt file `.gea` vào `Scripts/lib/`. Server lưu và gửi worker bản đã mở rộng. Tab **Components** trên web liệt kê các thư viện, cho tải file, tải script khởi tạo hoặc đặt hàng chính component. Chi tiết ở `sa-docs/gea-language-spec.md` mục 20.
- Máy phát điện làm mát bằng nước (`generator`): thân stator dạng hộp có gân đứng hai bên, hộp làm mát nước thò xuống dưới thân kèm ống nước vào/ra, hộp đầu cực + hai quạt trên đỉnh, hai nắp ổ trục + gối đỡ, trục rotor, tủ kích từ, bệ máy (khối đặc, không cắt lộ ruột rotor/lõi như ảnh). Luật ở `GeneratorParams.cs` (server dùng chung) và khối `FORM` của `generator.gea` giống nhau.
## Script `.gea` (kiểu SmartAssembly)

Đặc tả ngôn ngữ ở `sa-docs/gea-language-spec.md`. Trình thông dịch nằm ở `InventorAddin/InventorAddin.Core/Gea/` (lexer, parser, kiểm tra ngữ nghĩa, FORM, BUILD; không phụ thuộc Inventor), Inventor Host và builder ở `Services/Gea/`. Script mẫu: `InventorAddin/InventorAddin.Core/Scripts/` (`generator.gea` (máy phát điện), `turbine.gea` (turbine khí), `tank-level1.gea`, `transformer.gea`, `transformer-hv.gea`: máy biến áp ngoài trời chi tiết với cánh tản nhiệt dạng lá, bình dầu có giá đỡ, ống dầu, hai hàng sứ HV/LV, van xả, biển cảnh báo).

Dùng trong các add-in Transformer / Gas Turbine / Generator (cửa sổ là bảng **Script (.gea)**, chỉ hiện mẫu của sản phẩm, mẫu mặc định tự mở lần đầu):
1. **Open…** một file `.gea`, hoặc chọn **Template** rồi **Open template** (mở bản sao sửa được trong `%AppData%\InventorBoxAddin\scripts`).
2. Tab **Form** sinh từ khối `BEGIN_FORM`: ô ẩn/hiện theo `IF`, lỗi `MIN`/`MAX`/`ASSERT` tô đỏ đúng ô.
3. Sửa script ngay trong cửa sổ (lỗi có dòng, cột; nhấp đúp để nhảy tới dòng) hoặc **Edit externally**: lưu file là add-in tự nạp lại; bật *Apply automatically when the script file is saved* để model tự cập nhật.
4. **Apply**: lưu script, rồi trong **một Transaction** xóa mọi body/feature/sketch/work plane do script tạo lần trước, đồng bộ thông số thành User Parameter, chạy lại `BEGIN_BUILD`. Lỗi ở bất kỳ bước nào thì model giữ nguyên; Ctrl+Z hoàn tác cả lần Apply. Hình học người dùng tự vẽ (không mang dấu `GEA_Owner`) được giữ.
5. Script và giá trị được lưu trong Part (AttributeSet `GEA_Script`): mở lại `.ipt` là add-in nạp lại script để sửa tiếp (**Load from model**).

Builder có sẵn cho `CREATE_FEATURE builder:<Tên>`: `Tank`, `Cover`, `Block`, `Cylinder`, `Bushing`, `Radiator` (manifest ở `Gea/GeaManifests.cs`). Web worker dựng máy biến áp bằng `Scripts/transformer.gea` và hộp bằng `Scripts/box.gea`, nên file `.ipt` trả về cũng mở và sửa được bằng tab Script. Trên web, mỗi job dựng bằng script có nút **Export .gea** (script + giá trị đã đặt) và **Import .gea** ở trang New order dựng lại form từ file đó (xem `docs/r2-dashboard-gea-roundtrip-plan.md` và `sa-docs/gea-language-spec.md` mục 19).

## Cấu hình

- **Add-in**: `%AppData%\InventorBoxAddin\worker.json` (tự tạo lần đầu): `enabled`, `serverUrl`, `workerKey`, `workerName`, `pollSeconds`. Tên worker trên server là `<workerName>-<add-in>` (ví dụ `PC01-generator`). Log ở `%AppData%\InventorBoxAddin\logs`.
- **R2 (tùy chọn)**: `dotnet user-secrets set "R2:AccountId" ... --project be/Cadflow.Api` cho `R2:AccountId`, `R2:Bucket`, `R2:AccessKeyId`, `R2:SecretAccessKey` (hoặc biến môi trường `R2__AccountId`...; thêm `R2:Endpoint` nếu dùng jurisdiction EU). Có đủ thì worker tải kết quả thẳng lên bucket R2 (private) và web tải qua URL ký sẵn; không có thì lưu ở `data/artifacts` như cũ. Log khởi động ghi chế độ đang dùng.
- **Lưu giữ file**: `Retention:Days` (mặc định 0 = giữ mãi) xóa file kết quả của job đã xong quá số ngày đó; thông tin job vẫn còn.
- **Server**: `be/Cadflow.Api/appsettings.json`: `Urls`, `Worker:Key`, `ConnectionStrings:Cadflow`. **Đổi `Worker:Key` mặc định `dev-worker-key` trước khi mở ra ngoài localhost.**
- Muốn Inventor ở máy khác: đặt `Urls` thành `http://0.0.0.0:18080` và `serverUrl` của add-in trỏ tới máy chủ (nên đặt sau VPN/mạng riêng, xem mục 10 của kế hoạch).

## API worker (add-in gọi, header `X-Worker-Key`)

`POST /api/worker/heartbeat` · `POST /api/worker/claim` · `POST /api/worker/jobs/{id}/progress|artifacts|complete|fail?worker=...`

Job quá 90 giây không có nhịp tim thì tự trả về hàng đợi; tối đa 3 lần thử.
