# Thiết kế: danh mục component trên web (hướng A)

Ngày: 2026-11-10. Trạng thái: đã duyệt hướng thiết kế trong chat. Thuộc `docs/component-reuse-roadmap.md` (hướng A); dựa trên `INCLUDE lib:` (`2026-11-10-gea-include-design.md`).

## Mục tiêu

Người dùng xem được các component đang có (thư viện `Scripts/lib/*.gea`), tải file của chúng, và dùng chúng để tạo script mới hoặc đặt hàng ngay, không cần tự viết dòng `INCLUDE`. Các file `.gea` tải về phải **giải thích từng bước** của script.

## Ngoài phạm vi

Tải thư viện lên web (cần đăng nhập, kiểm tra an toàn, quyền sửa/xóa); chọn nhiều component trong một khung (hiện chỉ có một thư viện, làm khi có thư viện thứ hai); Chat/MCP; xuất file CAD riêng cho component (hướng B).

## 1. API (server, chỉ đọc)

| Endpoint | Trả về |
|---|---|
| `GET /api/components` | Danh sách component (xem dưới). |
| `GET /api/components/{name}/gea` | File thư viện gốc, tải về (`attachment; filename={name}.gea`, `text/plain; charset=utf-8`). |
| `GET /api/components/{name}/starter?productName=&prefix=` | Script khởi tạo, tải về (`{name}-starter.gea`). `productName` mặc định `"New product"`, `prefix` mặc định `MY_`; giá trị không hợp lệ trả 400. |

Mỗi phần tử của danh sách:

```json
{
  "name": "tank-base-cover",
  "title": "Tank, base and cover",
  "summary": "Component dùng lại: đế, thùng và nắp ...",
  "description": "<toàn bộ đoạn chú thích đầu file>",
  "prefixes": ["TR_"],
  "sharedVariables": [{"name":"hTank","type":"LENGTH"}, {"name":"hCover","type":"LENGTH"}, {"name":"hTop","type":"LENGTH"}, {"name":"tbcBody","type":"REFERENCE"}],
  "parameters": [{"name":"TR_TankLength","label":"Tank length","group":"Tank, base and cover","unit":"mm","type":"Length","default":2000,"min":1000,"max":5000,"required":true}],
  "errors": []
}
```

- `title` = `PRODUCT_NAME`. `description` = các dòng chú thích `!` liên tiếp ở đầu file (bỏ dòng gạch `====`, bỏ `!`). `summary` = đoạn đầu tiên của `description` (đến dòng trống hoặc dòng `!` rỗng).
- `parameters` lấy bằng cách biên dịch thư viện (`GeaScript.Compile`) rồi `EvaluateFormNumbers(null)`. `sharedVariables` quét dòng `DECLARE_VARIABLE <KIỂU> <tên>` và `DECLARE_REFERENCE <tên>` trong khối BUILD của thư viện.
- Thư viện biên dịch lỗi vẫn có trong danh sách: `errors` chứa thông báo (`GEA...`), `parameters` rỗng.
- Tên phải khớp `^[A-Za-z0-9_-]{1,64}$` (cùng quy tắc `INCLUDE`); tên lạ hoặc không có file trả 404. Mọi endpoint chỉ đọc; không có cách ghi.

## 2. Script khởi tạo

Sinh trên server (`ComponentStarter`), có chú thích tiếng Việt cho **từng phần**:

```
! ===== Script khởi tạo: dùng component "<title>" (thư viện <name>) =====
! (cách đọc file: phần đầu, FORM, BUILD; dòng bắt đầu bằng ! là chú thích)
! Phiên bản ngôn ngữ .gea. Bắt buộc, là lệnh đầu tiên.
GEA_VERSION 1
! Tên sản phẩm hiện trên web.
PRODUCT_NAME "<productName>"
! Tiền tố thông số riêng của bạn; tham số của component giữ tiền tố của nó (<prefixes>).
PARAM_PREFIX <prefix>
! PART = dựng một Part (.ipt).
TARGET PART

BEGIN_FORM
  ! Bước 1: chèn nhóm thông số của component (liệt kê tên các tham số).
  INCLUDE lib:<name>
  ! Bước 2: thông số của riêng bạn. Bỏ dấu ! để bật ví dụ:
  !   USER_INPUT_PARAM <prefix>Example LENGTH 100 mm MIN 10 MAX 500 LABEL "Example"
END_FORM

BEGIN_BUILD
  ! Bước 1: chèn các bước dựng của component. Sau dòng này dùng được các biến dùng chung: <danh sách>.
  INCLUDE lib:<name>
  ! Bước 2: bước dựng của riêng bạn. Ví dụ đặt một khối lên nắp (bỏ dấu ! để bật):
  !   CREATE_FEATURE builder:Block ...
END_BUILD
```

Khung phải **biên dịch không lỗi** và nhận được qua `POST /api/gea/import` mà chưa sửa gì. Ví dụ trong chú thích dùng đúng cú pháp builder đang có (`Block`), danh sách biến lấy từ `sharedVariables`. `productName` đi vào chuỗi nên chặn `"`, `\`, xuống dòng; `prefix` khớp `^[A-Za-z][A-Za-z0-9]{0,15}_$`.

## 3. Giải thích từng bước trong file `.gea`

- `tank-base-cover.gea` được chú thích lại: mỗi bước (khai báo biến, tính cao độ, dựng đế, dựng thùng, dựng nắp, đặt tên và gán thuộc tính) có chú thích nói ý nghĩa và công thức. Chỉ thêm chú thích: bộ test tương đương với `transformer.gea` gốc phải vẫn pass.
- **Quy ước** (ghi vào `sa-docs/gea-language-spec.md` mục 20): đoạn đầu của chú thích đầu file là mô tả ngắn hiện trên thẻ; mỗi **đoạn lệnh** (các dòng liên tiếp không có dòng trống xen giữa) trong khối FORM/BUILD của thư viện phải có ít nhất một dòng chú thích. Một test áp quy ước này cho mọi file trong `Scripts/lib`, nên thư viện thêm sau cũng phải giải thích.
- Chú thích bằng tiếng Việt như các script hiện có.

## 4. Giao diện (`fe/`)

- Tab **Components** (`#/components`) trong `App.jsx`; trang `pages/Components.jsx`.
- Mỗi component một thẻ: tiêu đề, mô tả (summary; mở rộng được để xem cả `description`), biến dùng chung, bảng tham số xếp theo nhóm (thu gọn được), hai dòng `INCLUDE` để copy, báo lỗi nếu `errors` không rỗng.
- Ba nút: **Download library (.gea)**; **Download starter script (.gea)**; **Order this component**. Nút thứ ba lấy script khởi tạo (`/starter`), gửi tới `/api/gea/import` có sẵn, lưu `sessionStorage['cadflow.import']` như `ImportGeaButton` rồi chuyển sang `#/import/<scriptId>`. Phần dùng chung được tách thành hàm `importScriptText(text, fileName)` trong `lib.js` để hai nơi dùng cùng một đường.
- Trạng thái tải, lỗi (mạng, 400/404) hiện bằng `Callout` như các trang khác.

## 5. Mã

`be/Cadflow.Api/Components.cs` (`ComponentCatalog`, `ComponentStarter`), `GeaLibraries` thêm `Names()`; endpoint trong `Program.cs`; `fe/src/pages/Components.jsx`; sửa `App.jsx`, `lib.js`, `components.jsx`, `app.css`.

## 6. Kiểm thử

`Cadflow.Api.Tests`:
- Danh sách có `tank-base-cover` với 7 tham số, tiền tố `TR_`, biến `hTank`/`hCover`/`hTop`/`tbcBody`, `summary` không rỗng, `errors` rỗng.
- Thư viện hỏng (thư mục thư viện tạm) vẫn có trong danh sách với `errors`; `description`/`summary` trích đúng.
- File tải về trùng byte với file gốc; tên lạ (`..`, `a/b`, không tồn tại) là 404.
- Script khởi tạo: biên dịch không lỗi; import qua `JobService.Import` trả 200 và form có tham số của component; tên sản phẩm và tiền tố tùy chỉnh được áp dụng; đầu vào không hợp lệ bị từ chối; có chú thích cho từng phần (header, FORM, BUILD, chỗ thêm tham số).
- Quy ước chú thích áp cho mọi file `Scripts/lib/*.gea`.
- Test tương đương `transformer.gea` vẫn pass.
- Giao diện: không có test tự động trong repo; kiểm tra bằng `vite build` và thử tay (liệt kê, ba nút, đặt hàng thử).

## Rủi ro

- API người dùng chưa có xác thực; các endpoint mới chỉ đọc thư viện đi kèm nên rủi ro thấp.
- Quét `DECLARE_*` bằng regex chỉ đúng với khai báo viết theo dòng đơn giản như hiện nay.
- Hiện chỉ có một thư viện nên giao diện chưa được thử với nhiều component; danh sách vẫn đúng với số lượng bất kỳ.
