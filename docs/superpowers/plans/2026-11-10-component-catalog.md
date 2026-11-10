# Kế hoạch triển khai: danh mục component trên web (hướng A)

Thiết kế: `docs/superpowers/specs/2026-11-10-component-catalog-design.md`. Mỗi task kết thúc bằng một commit; test chạy bằng `dotnet test be/Cadflow.Api.Tests` (cần PostgreSQL local, xem `docs/database.md`). Giao diện không có test tự động trong repo: kiểm bằng `npx vite build` (trong `fe/`) và thử tay. Không động tới `fe/src/pages/Chat.jsx` (đang được sửa riêng).

## Task 1: Chú thích từng bước trong `tank-base-cover.gea` + test quy ước

**File:** `InventorAddin/InventorAddin.Core/Scripts/lib/tank-base-cover.gea`. **Test mới:** `be/Cadflow.Api.Tests/ComponentCommentsTests.cs`.

1. Test trước (đỏ): với mỗi file `Scripts/lib/*.gea` trong thư mục output của test:
   - Cắt khối `BEGIN_FORM...END_FORM` và `BEGIN_BUILD...END_BUILD`; trong mỗi khối, tách thành **đoạn lệnh** (các dòng liên tiếp không có dòng trống) và khẳng định mỗi đoạn có ít nhất một dòng chú thích (`!` ở đầu dòng sau khi trim).
   - Chú thích đầu file (các dòng `!` trước `GEA_VERSION`) có ít nhất một đoạn mô tả (một dòng không phải gạch `====` và không rỗng).
   - Thông báo lỗi nêu tên file và dòng đầu của đoạn thiếu chú thích.
2. Chú thích lại thư viện (tiếng Việt, mỗi đoạn lệnh một dòng "Bước N: ..." kèm ý nghĩa/công thức): khai báo biến cao độ và biến tham chiếu; `hTank = TR_BaseThickness` (đáy thùng nằm trên đế), `hCover` (đáy nắp = đế + chiều cao thùng), `hTop` (đỉnh nắp); mỗi `CREATE_FEATURE builder:Block` (kích thước, vì sao nhô ra `TR_BaseOverhang`/`TR_CoverOverhang`, `Elevation`); `SET_REF_NAME`, `SET_REF_ATTR ... "ROLE"`, `SET_APPEARANCE`. Đoạn FORM (nhóm tham số) cũng có chú thích nói mỗi tham số là gì. **Chỉ thêm chú thích**, không đổi lệnh.
3. Chạy toàn bộ test: test quy ước xanh, `TransformerIncludeEquivalenceTests` vẫn xanh (không đổi hành vi).
4. Commit `Explain each step in tank-base-cover.gea; enforce comment convention for libraries`.

## Task 2: `ComponentCatalog` (danh sách + chi tiết)

**File mới:** `be/Cadflow.Api/Components.cs`. **Sửa:** `be/Cadflow.Api/GeaLibraries.cs` (thêm `Names()`: tên file `*.gea` trong `Scripts/lib` khớp `^[A-Za-z0-9_-]{1,64}$`, sắp xếp). **Test mới:** `ComponentCatalogTests.cs`.

1. Test trước (đỏ), dùng thư mục thư viện tạm để dựng thư viện giả (`ComponentCatalog` nhận một `Func<IEnumerable<string>> names` và `Func<string,string?> read`, mặc định trỏ vào `GeaLibraries`):
   - Thư viện thật `tank-base-cover`: `title` = `Tank, base and cover`, `prefixes` = `["TR_"]`, 7 tham số (`TR_TankLength` ... `TR_CoverOverhang`) có đủ nhãn, nhóm, đơn vị `mm`, mặc định, min, max, `required`; `sharedVariables` có `hTank`, `hCover`, `hTop` (LENGTH) và `tbcBody` (REFERENCE); `summary` không rỗng; `errors` rỗng.
   - `description`: các dòng `!` đầu file bỏ `!` và dòng gạch; `summary` = đoạn đầu (đến dòng `!` rỗng).
   - Thư viện hỏng (cú pháp sai): vẫn có trong danh sách, `errors` có mã `GEA...`, `parameters` rỗng.
   - `Get("..")`, `Get("a/b")`, `Get("missing")` trả null.
   - Sắp xếp theo tên; danh sách rỗng khi không có thư viện.
2. Cài đặt `ComponentCatalog.List()`, `Get(name)`, và DTO (record) `ComponentInfo`, `ComponentParam`, `ComponentVar`. Biên dịch bằng `GeaScript.Compile(text, GeaLibraries.Resolve, name + ".gea")`; tham số từ `EvaluateFormNumbers(null).Fields` (Name, Label, Group, Unit, Type, Min, Max, Required, giá trị mặc định lấy từ `Values`). Quét `sharedVariables` bằng regex trên khối BUILD của thư viện.
3. Commit `Add ComponentCatalog`.

## Task 3: `ComponentStarter` (script khởi tạo có chú thích)

**Sửa:** `be/Cadflow.Api/Components.cs`. **Test mới:** `ComponentStarterTests.cs`.

1. Test trước (đỏ):
   - `Build("tank-base-cover")` (mặc định): biên dịch không lỗi (`GeaScript.Compile(text, GeaLibraries.Resolve)`), có `INCLUDE lib:tank-base-cover` đúng hai lần (FORM và BUILD), `PARAM_PREFIX MY_`, `PRODUCT_NAME "New product"`.
   - `Build(name, productName: "Reactor", prefix: "RX_")` áp dụng đúng; form của bản biên dịch có tham số `TR_TankLength`.
   - `JobService.Import(starter)` trả 200, `form` có `TR_TankLength`; tạo job từ `scriptId` thành công (dùng `TestDb`).
   - Từ chối: `productName` có `"`, `\` hoặc xuống dòng hoặc dài hơn 80 ký tự; `prefix` không khớp `^[A-Za-z][A-Za-z0-9]{0,15}_$`; thư viện không có (trả lỗi, không ném ngoại lệ).
   - Chú thích đủ từng phần: chứa các dòng giải thích cho header (`GEA_VERSION`, `PRODUCT_NAME`, `PARAM_PREFIX`, `TARGET`), FORM, BUILD, chỗ thêm tham số riêng, và liệt kê các biến dùng chung của thư viện; quy ước "mỗi đoạn lệnh có chú thích" của Task 1 cũng đúng với script khởi tạo.
2. Cài đặt theo mẫu trong spec mục 2: chú thích đầu file, từng lệnh header có chú thích riêng, FORM/BUILD mỗi bước một chú thích, ví dụ `USER_INPUT_PARAM` và `CREATE_FEATURE builder:Block` được comment sẵn (dùng đúng cú pháp trong `transformer.gea`, đặt khối lên `hTop`).
3. Commit `Add ComponentStarter`.

## Task 4: Endpoint

**Sửa:** `be/Cadflow.Api/Program.cs` (đăng ký `ComponentCatalog` singleton). **Test:** thử bằng `curl` trên backend thật (không có hạ tầng test HTTP trong repo; logic đã được test ở Task 2-3).

1. `GET /api/components` → `ComponentCatalog.List()`; `GET /api/components/{name}/gea` → `Results.File(bytes, "text/plain; charset=utf-8", fileDownloadName: name + ".gea")`, 404 nếu `Get` null; `GET /api/components/{name}/starter` (query `productName`, `prefix`) → file `name-starter.gea`, 400 nếu đầu vào không hợp lệ, 404 nếu thiếu thư viện.
2. Build, restart backend (`kill` tiến trình đang nghe :18080 rồi `dotnet run --no-build`), kiểm tra bằng `curl`: danh sách, tải `gea` (so với file gốc bằng `diff`), `starter` (rồi `POST /api/gea/import` với nội dung đó), 404 cho `..` và tên lạ, 400 cho prefix xấu.
3. Commit `Add component catalog endpoints`.

## Task 5: Giao diện

**Sửa:** `fe/src/lib.js` (thêm `importScriptText(text, fileName)`), `fe/src/components.jsx` (`ImportGeaButton` dùng hàm mới, hành vi không đổi), `fe/src/App.jsx` (route `#/components`, mục NAV `Components`), `fe/src/app.css`. **Mới:** `fe/src/pages/Components.jsx`.

1. `importScriptText`: `postJson('/api/gea/import', { text })`, lưu `sessionStorage['cadflow.import']` (`scriptId`, `values`, `texts`, `warnings`, `hasValues`, `fileName`), `go('#/import/<id>')`; lỗi ném lại như hiện nay (có `e.body.diagnostics`).
2. `Components.jsx`: `api('/api/components')`, thẻ mỗi component (tiêu đề, summary, nút mở rộng description, biến dùng chung, bảng tham số theo nhóm trong `<details>`, khối hai dòng `INCLUDE` có nút Copy, `Callout` khi `errors`), ba nút: hai liên kết tải (`<a href download>` tới `/gea` và `/starter`) và nút **Order this component** (`fetch` starter → `importScriptText`). Trạng thái loading/lỗi bằng `Callout`.
3. `npx vite build` không lỗi (khôi phục `package-lock.json` nếu `npm install` làm đổi). Thử tay trên `http://localhost:5173/#/components` (Vite dev đang chạy): thấy thẻ `tank-base-cover`, tải được hai file, nút Order mở form `#/import/...` có các ô Tank, base and cover. Nếu có Chrome tự động hóa thì chụp màn hình; nếu không, nêu rõ phần nào chưa xem bằng mắt.
4. Commit `Add Components page`.

## Task 6: Tài liệu

- `sa-docs/gea-language-spec.md` mục 20: thêm tiểu mục "Danh mục component trên web" (endpoint, nút, script khởi tạo) và **quy ước chú thích thư viện** (đoạn đầu là mô tả, mỗi đoạn lệnh có chú thích, có test bắt buộc).
- `docs/component-reuse-roadmap.md`: đánh dấu hướng A đã xong, ghi những gì khác thiết kế (nếu có).
- `README.md`: một dòng về tab Components.
- Chạy toàn bộ test, commit `Document component catalog`.

## Nghiệm thu

- `dotnet test be/Cadflow.Api.Tests` toàn bộ xanh (80 test hiện có + test mới).
- Backend thật: `/api/components` trả `tank-base-cover`; file tải về đọc được, mỗi bước có giải thích; script khởi tạo import được và đặt đơn được (job `queued`).
- `git status` sạch trừ `Chat.jsx` (sửa riêng của người dùng); không commit `.env`, file build.
