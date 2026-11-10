# Đặc tả ngôn ngữ `.gea` (phiên bản 1, bản nháp)

File `.gea` là tương đương của file `.tab` (SmartAssembly) cho hệ thống Inventor và web của bạn. Tài liệu này chốt **cú pháp, kiểu, đơn vị, từng lệnh và hành vi** để có thể viết lexer, parser và thông dịch.

Đi kèm: `tank-level1.gea` (file mẫu cho Cấp 1, đúng theo đặc tả này).
Liên quan: `smartassembly-to-inventor-mapping.md` (ánh xạ từ `.tab`), `inventor-addin-plan.md`, `cadflow-order-to-model-plan.md`.

> Nếu ví dụ cú pháp ở mục 15 của `smartassembly-to-inventor-mapping.md` khác tài liệu này thì **tài liệu này là bản chuẩn**.

---

## 1. Vị trí và phạm vi

- `.gea` mô tả **một sản phẩm**: form nhập liệu, bảng tra, logic dựng model Inventor.
- Giữ phong cách `.tab`: mệnh lệnh, **mỗi dòng một lệnh**, từ khóa viết hoa, ba khối (form, bảng, dựng), không dùng cú pháp gọi hàm kiểu ngoặc đơn cho lệnh. Biểu thức vẫn có ngoặc và hàm tiện ích.
- Nơi chạy:

| Khối | Chạy ở | Khi nào |
|------|--------|---------|
| `BEGIN_FORM` | Server (và add-in) | Mỗi khi một giá trị trên form đổi, và khi gửi đơn |
| `BEGIN_TABLES` | Server và worker | Nạp một lần |
| `BEGIN_BUILD` | Worker (và add-in) | Khi chạy job |

- **Phạm vi v1:** toàn bộ lệnh **P0** (đủ cho Cấp 1: thùng, nắp, chân đế) được đặc tả đầy đủ. Lệnh **P1, P2, P3** chỉ đặt chỗ ở mục 11 để không bị đụng tên về sau.
- Khác `.tab` ở các điểm chính: mã hóa UTF-8; có đơn vị trong kiểu; khối FORM sinh form web và là hàm thuần; thông báo lỗi có mã, dòng, cột; tham chiếu gắn bằng thuộc tính; không có hộp thoại tương tác trong BUILD.

---

## 2. Quy ước từ vựng

| Hạng mục | Quy tắc |
|----------|---------|
| Mã hóa | UTF-8 (có thể có BOM). Tiếng Việt có dấu được phép trong chuỗi và chú thích |
| Dòng | Mỗi lệnh một dòng. Ký tự `\` ở cuối dòng nối với dòng kế tiếp |
| Thụt lề | Dùng Tab hoặc khoảng trắng để dễ đọc, **không mang ý nghĩa cú pháp** |
| Chú thích | `!` đến hết dòng; `/* ... */` nhiều dòng. Ký tự `!` trong chuỗi không phải chú thích |
| Từ khóa | **Viết hoa**, phân biệt hoa thường (`IF` đúng, `if` sai) |
| Định danh | `[A-Za-z_][A-Za-z0-9_]*`, phân biệt hoa thường. **Không cho hai tên chỉ khác nhau chữ hoa thường** (Inventor có thể không phân biệt) |
| Số nguyên | `12`, `-3` |
| Số thực | `3.5`, `0.3` (dấu chấm thập phân) |
| Chuỗi | `"..."`, thoát bằng `\"`, `\\`, `\n`, `\t` |
| Logic | `TRUE`, `FALSE` |
| Đại lượng có đơn vị | Số + đơn vị: `100 mm`, `45 deg` (cho phép viết liền `100mm`) |
| Đơn vị chiều dài | `mm`, `cm`, `m`, `in` |
| Đơn vị góc | `deg`, `rad` |
| Toán tử | `+ - * / %`, `== <> < <= > >=`, `AND OR NOT`, ngoặc `( )` |
| Tăng giảm | `i++`, `i--` (như một lệnh riêng) |
| Gán | `tên = biểu thức` (một lệnh riêng) |

Từ dành riêng: mọi từ khóa trong tài liệu này, cùng `TRUE`, `FALSE`, `ERROR`, `ERROR_MESSAGE`.

---

## 3. Cấu trúc file

```
GEA_VERSION 1                  (bắt buộc, dòng lệnh đầu tiên)
PRODUCT_NAME "..."             (bắt buộc)
PARAM_PREFIX XX_               (khuyến nghị)
TARGET PART                    (bắt buộc: PART hoặc ASSEMBLY)

BEGIN_FORM ... END_FORM        (tùy chọn)
BEGIN_TABLES ... END_TABLES    (tùy chọn)
BEGIN_BUILD ... END_BUILD      (bắt buộc)
```

- Thứ tự khối cố định: FORM, TABLES, BUILD.
- `PARAM_PREFIX`: mọi thông số khai báo trong FORM phải bắt đầu bằng tiền tố này (kiểm tra ở bước ngữ nghĩa). Có thể liệt kê nhiều tiền tố (`PARAM_PREFIX GT_ TR_`); xem mục 20 về `INCLUDE`. Mục đích: tránh đụng tên với Parameter của người dùng trong Inventor.
- `TARGET`: loại document mà BUILD tạo ra hoặc dùng. Việc mở document nào (đang mở hay tạo mới từ template) do **Host** quyết định, không do script.
- Biến khai báo trong một khối **chỉ có hiệu lực trong khối đó**. Chỉ **thông số** (khai báo bằng lệnh `*_PARAM`) được truyền từ FORM sang BUILD.

### 3.1. Lệnh nào được dùng ở khối nào

| Nhóm lệnh | Header | FORM | TABLES | BUILD |
|-----------|--------|------|--------|-------|
| `GEA_VERSION`, `PRODUCT_NAME`, `PARAM_PREFIX`, `TARGET` | ✓ | | | |
| `DECLARE_*`, gán, biểu thức | | ✓ | | ✓ |
| `IF`, `FOR`, `WHILE`, `BREAK`, `CONTINUE`, `RETURN` | | ✓ (không `WHILE`) | | ✓ |
| `BEGIN_GROUP`, `USER_INPUT_PARAM`, `CHECKBOX_PARAM`, `RADIOBUTTON_PARAM` | | ✓ | | |
| `ASSERT`, `PRINT` | | ✓ | | ✓ |
| `BEGIN_TABLE`, `COLUMN`, `ROW` | | | ✓ | |
| `LOOKUP` (P1) | | ✓ | | ✓ |
| `SET_PARAM`, `GET_PARAM`, `SET_PARAM_EXPR` | | | | ✓ |
| `CREATE_FEATURE`, `SET_REF_NAME`, `SET_REF_ATTR`, `GET_REF_*` | | | | ✓ |
| `SET_APPEARANCE`, `REGEN_MDL` | | | | ✓ |
| `PROCESSING_BOX_*` | | | | ✓ |
| `BEGIN_CATCH_ERROR`, `CLEAR_CATCH_ERROR`, `FAIL` | | | | ✓ |

Lệnh dùng sai khối là **lỗi ngữ nghĩa** (mã `GEA2020`), ví dụ `CREATE_FEATURE` trong FORM.

---

## 4. Kiểu và đơn vị

### 4.1. Các kiểu

| Kiểu | Mô tả | Ví dụ |
|------|-------|-------|
| `INTEGER` | Số nguyên | `3` |
| `DOUBLE` | Số thực, không đơn vị | `0.5` |
| `BOOL` | Logic | `TRUE` |
| `STRING` | Chuỗi | `"Grey"` |
| `LENGTH` | Chiều dài, lưu nội bộ theo mm | `100 mm` |
| `ANGLE` | Góc, lưu nội bộ theo độ | `30 deg` |
| `REFERENCE` | Tham chiếu tới đối tượng trong model (body, feature, ...) | |
| `ARRAY` | Danh sách phần tử cùng kiểu | P1 |
| `MAP` | Từ điển khóa chuỗi | P1 |
| `POINT`, `VECTOR`, `STOPWATCH` | | P2 |

Host chịu trách nhiệm đổi đơn vị khi gọi Inventor (đơn vị nội bộ của API là cm và radian). Script **không bao giờ** thấy cm hoặc radian.

### 4.2. Quy tắc phép tính

| Phép | Kết quả | Ghi chú |
|------|---------|---------|
| `LENGTH + LENGTH`, `LENGTH - LENGTH` | `LENGTH` | Tự đổi đơn vị (`1 cm + 5 mm` đúng) |
| `LENGTH * DOUBLE/INTEGER`, `DOUBLE * LENGTH`, `LENGTH / DOUBLE/INTEGER` | `LENGTH` | |
| `LENGTH / LENGTH` | `DOUBLE` | Tỉ số |
| `LENGTH * LENGTH` | **Lỗi** `GEA2002` | v1 chưa có diện tích |
| `ANGLE` | Tương tự `LENGTH` | |
| So sánh `LENGTH` với `LENGTH` | `BOOL` | So sánh `LENGTH` với số trần: **lỗi**, trừ khi số là `0` |
| `INTEGER` và `DOUBLE` | Tự nâng `INTEGER` lên `DOUBLE` | |
| Gán `DOUBLE` vào `LENGTH` | **Lỗi** | Phải nhân với `1 mm` |
| `STRING + STRING` | `STRING` | Nối chuỗi. `STR(x)` đổi số sang chuỗi |
| `%` | Chỉ cho `INTEGER` | |

Chia cho 0 là lỗi thực thi `GEA5002`.

---

## 5. Khai báo và biến

| Lệnh | Cú pháp | Ghi chú |
|------|---------|---------|
| Biến | `DECLARE_VARIABLE <kiểu> <tên> [giá trị]` | Kiểu: `INTEGER`, `DOUBLE`, `BOOL`, `STRING`, `LENGTH`, `ANGLE`. Không có giá trị thì lấy mặc định của kiểu (0, `FALSE`, chuỗi rỗng) |
| Tham chiếu | `DECLARE_REFERENCE <tên>` | Giá trị ban đầu là "chưa gán" |
| Mảng (P1) | `DECLARE_ARRAY <tên> [<kiểu>]` | Mặc định kiểu `REFERENCE` |
| Từ điển (P1) | `DECLARE_MAP <kiểu> <tên>` | Khóa luôn là `STRING` |
| Gán | `<tên> = <biểu thức>` | Kiểu phải hợp lệ theo mục 4.2. Thông số từ FORM **chỉ đọc** trong BUILD |
| Tăng, giảm | `<tên>++`, `<tên>--` | Chỉ cho `INTEGER` |

Quy tắc:
- Khai báo nên đặt ở đầu khối, gom theo kiểu (khuyến nghị, không bắt buộc).
- Dùng tên chưa khai báo: `GEA2001`. Khai báo trùng tên: `GEA2003`.
- Biến `ERROR` (`BOOL`) và `ERROR_MESSAGE` (`STRING`) có sẵn, chỉ đọc (xem mục 9.6).

---

## 6. Luồng điều khiển

| Cấu trúc | Cú pháp | Ghi chú |
|----------|---------|---------|
| Điều kiện | `IF <bt>` / `ELSE_IF <bt>` / `ELSE` / `END_IF` | `<bt>` phải là `BOOL` |
| Vòng đếm | `FOR <i> FROM <a> TO <b> [STEP <s>]` ... `END_FOR` | `a`, `b`, `s` là `INTEGER`. **`TO` bao gồm giá trị cuối.** `STEP` mặc định 1, âm được, khác 0 |
| Duyệt mảng (P1) | `FOR <x> REF <mảng>` ... `END_FOR` | Xem quy tắc không xóa bên dưới |
| Duyệt từ điển (P1) | `FOR <x> REF <map>` ... `END_FOR` | Dùng `x.key`, `x.value` |
| Vòng điều kiện | `WHILE <bt>` ... `END_WHILE` | Chỉ trong BUILD. Giới hạn số vòng, xem dưới |
| Thoát vòng | `BREAK`, `CONTINUE` | |
| Kết thúc sớm | `RETURN` | Trong BUILD: kết thúc BUILD thành công. Trong FORM: kết thúc đánh giá FORM |

- **Biến lặp** `i` của `FOR ... FROM` được tạo ngầm, kiểu `INTEGER`, chỉ tồn tại trong vòng.
- **Giới hạn số vòng**: mỗi `WHILE` và `FOR` tối đa **10000 vòng** (cấu hình được). Vượt quá: `GEA5001`, job thất bại.
- **Không xóa phần tử của mảng khi đang duyệt nó**: thông dịch duyệt trên **bản sao** nên không lỗi; nếu thấy cần xóa, dùng `FOR ... FROM ... STEP -1` theo chỉ số.
- Chỉ số mảng **bắt đầu từ 0**.

---

## 7. Khối FORM

Khối FORM mô tả **form web** và các luật kiểm tra. Đây là một **hàm thuần**: cùng giá trị nhập vào luôn cho cùng kết quả, không đụng tới Inventor.

### 7.1. Lệnh

| Lệnh | Cú pháp | Ý nghĩa |
|------|---------|---------|
| Nhóm | `BEGIN_GROUP "<tiêu đề>"` ... `END_GROUP` | Gom các ô thành một vùng trên form (như các khung Rating, Cooling, Options trong video) |
| Ô nhập | `USER_INPUT_PARAM <tên> <kiểu> <mặc định> [<đơn vị>] [MIN <v>] [MAX <v>] [STEP <v>] [REQUIRED] [LABEL "<chữ>"] [TOOLTIP "<chữ>"]` | Thông số nhập. Kiểu: `LENGTH`, `ANGLE`, `INTEGER`, `DOUBLE`, `STRING` |
| Ô tích | `CHECKBOX_PARAM <tên> <TRUE\|FALSE> [LABEL "<chữ>"] [TOOLTIP "<chữ>"]` | Thông số `BOOL` |
| Nút chọn một | `RADIOBUTTON_PARAM <tên> "<a>" "<b>" ... [DEFAULT "<x>"] [LABEL "<chữ>"]` | Thông số `STRING`. Mặc định là lựa chọn đầu, trừ khi có `DEFAULT` |
| Luật | `ASSERT <bt> "<thông báo>" [WARN]` | Sai thì báo lỗi (hoặc cảnh báo nếu có `WARN`) |
| Chữ thông báo | `PRINT <biểu thức>` | Trong FORM, hiện thành ghi chú trên form khi dòng này được chạy tới |

Với `USER_INPUT_PARAM`:
- `<mặc định>` là một số (có thể kèm đơn vị cho `LENGTH` và `ANGLE`). Ví dụ `LENGTH 100 mm`.
- `MIN`, `MAX`, `STEP` là số; **nếu không ghi đơn vị thì dùng đơn vị của thông số**. Ví dụ `MIN 40 MAX 500` cho `LENGTH 100 mm` nghĩa là 40 mm đến 500 mm.
- `REQUIRED`: giá trị không được rỗng. Với số có mặc định thì luôn thỏa.
- `LABEL` mặc định là tên thông số.
- Vi phạm `MIN`/`MAX` tự sinh lỗi dạng: *"TR_Wall phải nằm trong [1 mm, 10 mm]"*.

Trong FORM cũng dùng được `DECLARE_VARIABLE`, gán, `IF/ELSE_IF/ELSE/END_IF`, `FOR`, `RETURN`, `LOOKUP` (P1).

### 7.2. Hiện hoặc ẩn ô

Đặt lệnh `*_PARAM` bên trong `IF ... END_IF`. Khi điều kiện sai, ô **ẩn**. Quy tắc:
- Ô ẩn nhận **giá trị mặc định** và vẫn đọc được trong BUILD.
- Ô ẩn **không** kiểm tra `MIN`, `MAX`, `REQUIRED`.
- Thông số vẫn tồn tại như thể được khai báo; nó chỉ không hiện ra để nhập.

### 7.3. Cách tính khối FORM

1. Đầu vào: bản đồ `tên -> giá trị` người dùng đang nhập. Ô thiếu dùng mặc định.
2. Chạy khối **từ trên xuống dưới**. Dùng thông số trước khi khai báo là lỗi.
3. Đầu ra:

| Trường | Ý nghĩa |
|--------|---------|
| Danh sách ô | Với từng thông số: hiện hay ẩn, giá trị cuối (sau khi áp mặc định cho ô ẩn), hợp lệ hay không |
| Lỗi | Các `ASSERT` sai, vi phạm `MIN`/`MAX`/`REQUIRED`, kèm thông báo |
| Cảnh báo | Các `ASSERT ... WARN` sai |
| Ghi chú | Các `PRINT` đã chạy |

4. **Gửi đơn chỉ được khi không còn lỗi.** Server chạy lại khối FORM khi nhận đơn, không tin kết quả từ trình duyệt.
5. Giá trị trước khi đổi lựa chọn bị ẩn sẽ mất (đưa về mặc định). Nếu muốn nhớ lại khi ô hiện lên lần nữa thì đó là việc của giao diện, không phải ngôn ngữ.

---

## 8. Khối TABLES (P1)

Mô tả bảng tra (catalog kích thước), không phải bảng giá.

```
BEGIN_TABLES
  BEGIN_TABLE Rating
    COLUMN kVA INTEGER
    COLUMN Length LENGTH mm
    ROW 630  1800
    ROW 1000 2100
  END_TABLE
END_TABLES
```

| Lệnh | Cú pháp | Ý nghĩa |
|------|---------|---------|
| `BEGIN_TABLE` ... `END_TABLE` | `BEGIN_TABLE <tên>` | Một bảng |
| `COLUMN` | `COLUMN <tên> <kiểu> [<đơn vị>]` | Cột, theo thứ tự |
| `ROW` | `ROW <giá trị> ...` | Hàng dữ liệu, số giá trị bằng số cột |
| `SOURCE` | `SOURCE "<đường dẫn>"` | Thay cho `ROW`: nạp từ CSV hoặc XLSX trong thư mục sản phẩm |
| `LOOKUP` | `LOOKUP <bảng> <cột khóa> <khóa> <cột lấy> <biến>` | Gán biến bằng giá trị cột `<cột lấy>` của hàng có `<cột khóa> == <khóa>`. Không có hàng nào: `GEA4030` |

---

## 9. Khối BUILD

### 9.1. Mô hình thực thi

1. Host chuẩn bị document theo `TARGET` (trong add-in: document đang mở; ở worker: document mới từ template).
2. Host **đồng bộ mọi thông số FORM thành User Parameter** có cùng tên, đúng đơn vị, trước dòng đầu tiên của BUILD. Nếu đã có Parameter trùng tên mà **không do `.gea` này tạo** (không có dấu sở hữu): `GEA4010`, dừng.
3. Host mở **một Transaction** bao toàn bộ BUILD.
4. Chạy từng dòng. Lỗi không được bắt, hoặc `FAIL`, làm **hủy Transaction** (model về nguyên trạng) và job thất bại.
5. Chạy hết `END_BUILD` hoặc gặp `RETURN` không lỗi: **xác nhận Transaction**.

BUILD **không có hộp thoại**. Không có lệnh nào chờ người dùng.

### 9.2. Thông số

| Lệnh | Cú pháp | Ý nghĩa |
|------|---------|---------|
| `SET_PARAM` | `SET_PARAM <tên> <biểu thức>` | Đặt giá trị cho User Parameter, tạo nếu chưa có. Kiểu và đơn vị lấy theo biểu thức |
| `SET_PARAM_EXPR` | `SET_PARAM_EXPR <tên> "<chuỗi biểu thức>"` | Đặt **biểu thức của Inventor** (ví dụ `"TR_TankLength / 2"`), để Parameter phụ thuộc Parameter khác trong model |
| `GET_PARAM` | `GET_PARAM <tên> <biến>` | Đọc giá trị Parameter vào biến, đúng kiểu và đơn vị |

Parameter tạo bằng `SET_PARAM` cũng được gắn dấu sở hữu của file `.gea` này.

### 9.3. Dựng bằng builder: `CREATE_FEATURE`

```
CREATE_FEATURE <nguồn> [ON <tham chiếu>]
  FEATURE_IN "<tên đầu vào>" <biểu thức hoặc tham chiếu>
  ...
  FEATURE_OUT <biến> "<tên đầu ra>"
  ...
END_CREATE_FEATURE
```

| Phần | Ý nghĩa |
|------|---------|
| `<nguồn>` | `builder:<Tên>`: hàm dựng viết bằng C#. (P1) `lib:<Đường dẫn>`: iFeature, iPart hoặc assembly mẫu trong thư viện sản phẩm |
| `ON <tham chiếu>` | Tùy chọn. Đối tượng đích mà builder tác động lên (ví dụ thùng để khoét lỗ) |
| `FEATURE_IN` | Truyền một đầu vào **theo tên**. Kiểu và đơn vị phải khớp **manifest** của builder |
| `FEATURE_OUT` | Gán đối tượng đầu ra (theo tên) vào biến `REFERENCE` đã khai báo |

Quy tắc kiểm tra (ở bước ngữ nghĩa, khi đã có manifest):
- Đầu vào không có trong manifest: `GEA2010`.
- Thiếu đầu vào bắt buộc: `GEA2011`.
- Đầu ra không có trong manifest: `GEA2012`.
- Sai kiểu hoặc đơn vị: `GEA2002`.
- Nguồn không tồn tại: `GEA2013`.

Theo **tên**, không theo chỉ số, để thư viện đổi thứ tự không làm vỡ script.

### 9.4. Tham chiếu

| Lệnh | Cú pháp | Ý nghĩa | Ưu tiên |
|------|---------|---------|---------|
| `SET_REF_NAME` | `SET_REF_NAME <ref> "<tên>"` | Đổi tên đối tượng. Tên kết thúc bằng `*` thì Host thêm hậu tố để duy nhất (`TR_Tank_1`...). Trùng tên mà không có `*`: `GEA4011` | P0 |
| `GET_REF_NAME` | `GET_REF_NAME <ref> <biến>` | Đọc tên | P0 |
| `SET_REF_ATTR` | `SET_REF_ATTR <ref> "<khóa>" <biểu thức>` | Gắn thuộc tính (lưu trong file, còn sau khi mở lại), dùng để tìm lại | P0 |
| `GET_REF_ATTR` | `GET_REF_ATTR <ref> "<khóa>" <biến>` | Đọc thuộc tính | P0 |
| `REF_VALID` | `REF_VALID <ref> <biến BOOL>` | Còn hợp lệ không | P1 |
| `SEARCH_MDL_REF`, `SEARCH_MDL_REFS` | `SEARCH_MDL_REF <biến> TYPE <loại> [NAME "<mẫu>"] [ATTR "<khóa>" <giá trị>]` | Tìm một hoặc nhiều đối tượng. Loại: `BODY`, `FEATURE`, `SKETCH`, `WORKPLANE`, `OCCURRENCE`, `PARAMETER` (P2: `FACE`, `EDGE`). Mẫu tên dùng `*` và `?` | P1 |
| `INVALIDATE_REF`, `COPY_REF`, `REF_EQUAL` | | | P1 |

Dùng tham chiếu chưa gán: `GEA4012`. Quy ước thuộc tính chuẩn: khóa `ROLE` cho vai trò (`TANK`, `COVER`, ...).

### 9.5. Ngoại hình và cập nhật

| Lệnh | Cú pháp | Ý nghĩa |
|------|---------|---------|
| `SET_APPEARANCE` | `SET_APPEARANCE <ref> <biểu thức chuỗi>` | Gán ngoại hình (màu, vật liệu hiển thị) theo **tên** trong thư viện ngoại hình đã cấu hình. Không có: `GEA4020` |
| `REGEN_MDL` | `REGEN_MDL` | Cập nhật model. Thất bại đặt `ERROR` nếu đang trong khối bắt lỗi, nếu không thì job thất bại |

### 9.6. Tiến trình và nhật ký

| Lệnh | Cú pháp | Ý nghĩa |
|------|---------|---------|
| `PROCESSING_BOX_START` | `PROCESSING_BOX_START "<tiêu đề>"` | Bắt đầu một nhóm bước (tùy chọn) |
| `PROCESSING_BOX_SET_STATE` | `PROCESSING_BOX_SET_STATE "<chữ>" [<phần trăm>]` | Cập nhật bước hiện tại. Ở worker, hiện ngay trên trang job (như dòng "đang dựng assembly" trong video) |
| `PROCESSING_BOX_END` | `PROCESSING_BOX_END` | Kết thúc nhóm |
| `PRINT` | `PRINT <biểu thức>` | Ghi vào **nhật ký job**, kèm số dòng |

### 9.7. Xử lý lỗi

| Lệnh | Cú pháp | Ý nghĩa |
|------|---------|---------|
| `BEGIN_CATCH_ERROR` ... `END_CATCH_ERROR` | | Bắt lỗi thực thi của Host trong khối |
| `ERROR` | `BOOL`, chỉ đọc | `TRUE` nếu khối vừa rồi có lỗi |
| `ERROR_MESSAGE` | `STRING`, chỉ đọc | Nội dung lỗi |
| `CLEAR_CATCH_ERROR` | | Xóa `ERROR` và `ERROR_MESSAGE` |
| `FAIL` | `FAIL <biểu thức chuỗi>` | Chủ động làm job thất bại với thông báo. Trong khối bắt lỗi thì chỉ đặt `ERROR` |

Quy tắc (cải tiến so với `.tab`):
- **Vào `BEGIN_CATCH_ERROR` thì `ERROR` tự đưa về `FALSE`**, không cần nhớ gọi `CLEAR_CATCH_ERROR` để tránh dương tính giả.
- Khi có lỗi bên trong khối, **phần còn lại của khối bị bỏ qua**, chạy tiếp từ sau `END_CATCH_ERROR`, và `ERROR` là `TRUE`.
- Lỗi do `ASSERT` sai trong BUILD không bị bắt: job thất bại.
- Lỗi cú pháp, ngữ nghĩa không bao giờ bị bắt (chúng được phát hiện trước khi chạy).
- **Cần kiểm chứng:** khi lỗi xảy ra bên trong khối bắt lỗi, các thay đổi **dở dang của chính khối đó** có được hoàn tác hay không phụ thuộc vào việc Inventor có hỗ trợ giao dịch lồng nhau như mong đợi. Cho tới khi kiểm chứng, giả định an toàn: sau một khối bắt lỗi thất bại, **không dựa vào trạng thái model** của khối đó, hoặc dùng `FAIL`.

---

## 10. Hàm dựng sẵn trong biểu thức

| Hàm | Ý nghĩa |
|-----|---------|
| `MIN(a, b)`, `MAX(a, b)` | Nhỏ nhất, lớn nhất. Hai đối số cùng chiều (cùng kiểu hoặc cùng đơn vị) |
| `ABS(a)` | Trị tuyệt đối |
| `ROUND(a, n)`, `FLOOR(a)`, `CEIL(a)` | Làm tròn. `n` là số chữ số thập phân. Với `LENGTH` giữ đơn vị |
| `SQRT(a)` | Căn bậc hai, chỉ cho `DOUBLE` |
| `STR(a)` | Đổi số hoặc đại lượng sang chuỗi (có đơn vị) |
| `NUM(s)` | Đổi chuỗi sang `DOUBLE`. Lỗi `GEA5003` nếu không phải số |
| `FIND(s, t)` | Vị trí chuỗi con `t` trong `s`, `-1` nếu không có |

---

## 11. Lệnh đặt chỗ (chưa đặc tả ở v1)

Các tên sau **bị dành riêng** để sau này thêm không đụng tên.

| Ưu tiên | Lệnh |
|---------|------|
| P1 | `CHOICE_PARAM`, `MESSAGE_BOX`, `MESSAGE_BOX_EX`, `SET_OUTPUT`, `JSON_LOAD_DOCUMENT`, `JSON_GET_VALUE`, `JSON_SET_VALUE`, `TABLE_*`, `ADD_ARRAY_ELEM`, `GET_ARRAY_ELEM`, `GET_ARRAY_SIZE`, `CLEAR_ARRAY`, `SET_MAP_VALUE`, `GET_MAP_VALUE`, `HAS_MAP_KEY` |
| P2 | `ADD_COMPONENT`, `BEGIN_ASSEMBLE`/`END_ASSEMBLE` với `MATE`, `FLUSH`, `INSERT`, `ANGLE_CONSTRAINT`; `EXPORT_STEP`, `EXPORT_IMAGE`, `EXPORT_PDF`; `DRW_NEW`, `DRW_ADD_VIEW`, `DRW_ADD_SECTION`, `DRW_ADD_BALLOONS`, `DRW_ADD_PARTSLIST`, `DRW_ADD_NOTE`; `DECLARE_STRUCT`, `START_STOPWATCH`, `GET_STOPWATCH_TIME`; `FILE_OPEN`, `FILE_READ_LINE`, `FILE_WRITE_LINE`, `FILE_CLOSE` (chỉ trong thư mục job) |
| P3 | `USER_SELECT`, `USER_SELECT_MULTIPLE` (chỉ add-in), `GET_SURFACE_NORM`, `MEASURE_DISTANCE`, `GET_SURFACE_CONTOUR`, `GET_REF_VERTEX`, `GET_REF_POS`, `VECTOR_NORMALIZE`, `VECTOR_FROM_POINTS`, `FIX_FAIL_FEATURE` |

Các lệnh **chạy chương trình ngoài** (như `.exe`, PowerShell) **không có** trong ngôn ngữ ở v1 và mặc định bị cấm.

---

## 12. Manifest của builder

Mỗi builder (hàm C# dựng hình) đăng ký một **manifest** để bước ngữ nghĩa kiểm tra `CREATE_FEATURE` mà không cần Inventor. Đây là hợp đồng giữa script và thư viện.

Nội dung khái niệm của manifest:

| Trường | Ý nghĩa |
|--------|---------|
| `name`, `version` | Tên và phiên bản builder |
| `target` | `PART` hoặc `ASSEMBLY` mà builder tác động |
| `inputs` | Danh sách đầu vào: tên, kiểu (kể cả `LENGTH`, `REFERENCE`), bắt buộc hay không, mặc định |
| `outputs` | Danh sách đầu ra: tên, kiểu (thường `REFERENCE`) |
| `simulate` | Cách **mô phỏng Host** thể hiện builder: khối bao (kích thước), vị trí, khối lượng ước tính theo công thức đơn giản. Dùng cho xem trước và chạy thử |

Ví dụ nội dung manifest của builder `Tank` (dạng JSON, chỉ minh họa):

```json
{
  "name": "Tank",
  "version": "1.0",
  "target": "PART",
  "inputs": [
    { "name": "Length", "type": "LENGTH", "required": true },
    { "name": "Width", "type": "LENGTH", "required": true },
    { "name": "Height", "type": "LENGTH", "required": true },
    { "name": "Wall", "type": "LENGTH", "required": true },
    { "name": "Bottom", "type": "LENGTH", "required": true },
    { "name": "CornerRadius", "type": "LENGTH", "required": false, "default": "0 mm" },
    { "name": "InnerRadius", "type": "LENGTH", "required": false, "default": "0 mm" },
    { "name": "SkirtHeight", "type": "LENGTH", "required": false, "default": "0 mm" }
  ],
  "outputs": [ { "name": "Body", "type": "REFERENCE" } ],
  "simulate": { "boundingBox": ["Length", "Width", "Height + SkirtHeight"] }
}
```

Job ghi lại **phiên bản của mỗi builder** đã dùng.

---

## 13. Chẩn đoán lỗi

Định dạng: `<file>(<dòng>,<cột>): <mức> GEAnnnn: <thông báo>`. Mức: `error`, `warning`.

| Dải mã | Loại | Ví dụ |
|--------|------|-------|
| `GEA1xxx` | Cú pháp | `GEA1001` lệnh không nhận ra; `GEA1002` thiếu `END_*`; `GEA1003` sai số tham số; `GEA1004` chuỗi chưa đóng; `GEA1010-1015` lỗi `INCLUDE` (mục 20) |
| `GEA2xxx` | Ngữ nghĩa | `GEA2001` tên chưa khai báo; `GEA2002` sai kiểu hoặc đơn vị; `GEA2003` khai báo trùng; `GEA2010-2013` lỗi `CREATE_FEATURE`; `GEA2020` lệnh dùng sai khối; `GEA2030` thiếu tiền tố thông số |
| `GEA3xxx` | Kiểm tra giá trị | `GEA3001` `ASSERT` sai; `GEA3002` vi phạm `MIN`/`MAX`; `GEA3003` thiếu giá trị `REQUIRED` |
| `GEA4xxx` | Thực thi trên Host | `GEA4010` Parameter trùng không thuộc `.gea`; `GEA4011` trùng tên đối tượng; `GEA4012` tham chiếu chưa gán; `GEA4020` ngoại hình không có; `GEA4030` không có hàng trong bảng; `GEA4099` lỗi Inventor chưa phân loại (kèm nội dung gốc) |
| `GEA5xxx` | Giới hạn | `GEA5001` vượt số vòng; `GEA5002` chia cho 0; `GEA5003` lỗi đổi chuỗi sang số; `GEA5004` quá thời gian |

Lỗi `GEA1xxx` và `GEA2xxx` phát hiện **trước khi chạy** và liệt kê hết một lần (không dừng ở lỗi đầu tiên khi có thể). Lỗi `GEA3xxx` hiện trên form. Lỗi `GEA4xxx` và `GEA5xxx` ghi vào nhật ký job kèm dòng, cột, lệnh.

Thông dịch ghi **vết thực thi** (dòng nào đã chạy) để tô sáng trên trang job và trong trình soạn thảo.

---

## 14. Hành vi ở hai Host (lệnh P0)

| Lệnh | Inventor Host | Mô phỏng Host |
|------|----------------|----------------|
| `*_PARAM` (FORM) | Không chạy ở Inventor | Chạy bình thường (FORM là hàm thuần) |
| Đồng bộ thông số đầu BUILD | Tạo hoặc cập nhật User Parameter đúng đơn vị, gắn dấu sở hữu | Ghi vào model ảo |
| `SET_PARAM`, `SET_PARAM_EXPR`, `GET_PARAM` | Đọc, ghi User Parameter | Đọc, ghi model ảo (biểu thức chuỗi chỉ lưu, không tính lại, trừ biểu thức đơn giản) |
| `CREATE_FEATURE builder:X` | Chạy builder C# gọi Inventor API trong Transaction | Dùng `simulate` của manifest để thêm khối ảo; ghi vết |
| `SET_REF_NAME`, `GET_REF_NAME` | Đổi tên, đọc tên đối tượng Inventor | Đổi tên đối tượng ảo |
| `SET_REF_ATTR`, `GET_REF_ATTR` | Gắn, đọc AttributeSet | Lưu thuộc tính ảo |
| `SET_APPEARANCE` | Gán Appearance theo tên | Ghi vào vết (để vẽ màu xem trước) |
| `REGEN_MDL` | Cập nhật document | Không làm gì |
| `PROCESSING_BOX_*` | Gửi bước lên server cho trang job | Ghi vào vết |
| `PRINT` | Nhật ký job | Ghi vào vết |
| `BEGIN_CATCH_ERROR` | Bắt ngoại lệ từ Inventor | Bắt lỗi từ simulate (nếu có) |
| `FAIL`, `ASSERT` | Hủy Transaction, job thất bại | Báo lỗi trong kết quả chạy thử |

Lệnh hay đoạn phụ thuộc hình học thật (P1 trở đi, như tìm mặt, cạnh) ở mô phỏng Host sẽ **đánh dấu "không mô phỏng được"** trong kết quả chạy thử.

---

## 15. Ngữ pháp rút gọn (cho người viết parser)

```
file        := header form? tables? build
header      := GEA_VERSION int NL
               PRODUCT_NAME string NL
               (PARAM_PREFIX ident NL)?
               TARGET (PART | ASSEMBLY) NL
form        := BEGIN_FORM NL stmt* END_FORM NL
tables      := BEGIN_TABLES NL table* END_TABLES NL
build       := BEGIN_BUILD NL stmt* END_BUILD NL

stmt        := command NL | assign NL | incdec NL | block
command     := KEYWORD arg*                 (hình dạng arg theo bảng lệnh)
assign      := ident '=' expr
incdec      := ident ('++' | '--')
block       := if | for | while | group | catch | create_feature
if          := IF expr NL stmt* (ELSE_IF expr NL stmt*)* (ELSE NL stmt*)? END_IF NL
for         := FOR ident FROM expr TO expr (STEP expr)? NL stmt* END_FOR NL
             | FOR ident REF ident NL stmt* END_FOR NL
while       := WHILE expr NL stmt* END_WHILE NL
group       := BEGIN_GROUP string NL stmt* END_GROUP NL
catch       := BEGIN_CATCH_ERROR NL stmt* END_CATCH_ERROR NL
create_feature := CREATE_FEATURE source (ON ident)? NL
                  (FEATURE_IN string expr NL | FEATURE_OUT ident string NL)*
                  END_CREATE_FEATURE NL

expr        := or
or          := and (OR and)*
and         := not (AND not)*
not         := NOT not | cmp
cmp         := add ((== | <> | < | <= | > | >=) add)?
add         := mul ((+ | -) mul)*
mul         := unary ((* | / | %) unary)*
unary       := '-' unary | primary
primary     := number unit? | string | TRUE | FALSE | ident | ident '.' ident
             | FUNC '(' (expr (',' expr)*)? ')' | '(' expr ')'
```

Gợi ý hiện thực: mỗi lệnh có một **bảng khai báo hình dạng** (tên lệnh, danh sách tham số theo vị trí với kiểu, từ khóa tùy chọn, có mở khối hay không, khối nào được dùng). Parser chung đọc bảng này, nên thêm lệnh mới chủ yếu là thêm một dòng vào bảng và một hàm xử lý ở mỗi Host.

---

## 16. Phiên bản và tương thích

- `GEA_VERSION <số>` bắt buộc ở dòng lệnh đầu. Thông dịch từ chối file có phiên bản cao hơn mình hỗ trợ.
- Thêm lệnh mới trong cùng phiên bản chính là tương thích ngược. Đổi nghĩa lệnh có sẵn thì tăng phiên bản.
- Mỗi job ghi lại **phiên bản ngôn ngữ**, **phiên bản sản phẩm** và **phiên bản của từng builder** để lặp lại được.

---

## 17. Quyết định mở và rủi ro

| # | Vấn đề | Tạm chọn trong bản nháp | Cần chốt |
|---|--------|--------------------------|----------|
| Q1 | `INCLUDE` dùng chung biến với file cha hay cô lập | **Đã chốt: dùng chung biến** (mục 20) | Không còn |
| Q2 | Chỉ số mảng bắt đầu từ 0 | Từ 0 | Xác nhận |
| Q3 | `FOR ... TO` bao gồm giá trị cuối | Bao gồm | Xác nhận |
| Q4 | Ô ẩn nhận giá trị mặc định và vẫn đọc được | Như mô tả mục 7.2 | Xác nhận |
| Q5 | Thông số do FORM tạo luôn đồng bộ thành User Parameter | Luôn đồng bộ | Xác nhận |
| Q6 | Khối bắt lỗi có hoàn tác phần dở dang không | Chưa đảm bảo (mục 9.7) | **Cần kiểm chứng với Inventor** |
| Q7 | Nguồn thư viện ngoại hình (`SET_APPEARANCE`) | Theo tên trong thư viện cấu hình | Chọn nơi lưu |
| Q8 | Tên API Inventor cụ thể cho thuộc tính, giao dịch, ngoại hình | Chưa xác định | Kiểm tra với tài liệu Inventor 2027 khi hiện thực |

Rủi ro chính:
- **Phình lệnh quá sớm**: chỉ hiện thực P0 cho tới khi Cấp 1 chạy được cả hai Host.
- **Hai Host lệch nhau**: mỗi lệnh phải có hành vi rõ ở cả hai Host (bảng mục 14) và có bộ test so sánh.
- **Manifest lệch với builder thật**: đặt manifest và builder trong cùng một project, có test kiểm tra manifest khớp chữ ký builder.
- **Người viết script gặp lỗi khó hiểu**: thông báo lỗi có mã, dòng, cột ngay từ đầu.

---

## 18. Việc làm ngay tiếp theo

1. Xác nhận Q2 đến Q5, đồng ý hay đổi bản nháp.
2. Đọc `tank-level1.gea` và chỉnh cú pháp nếu thấy chưa tự nhiên (đây là bước D0).
3. Viết **bảng khai báo hình dạng lệnh P0** (mục 15) thành dữ liệu.
4. Làm lexer, parser và bước ngữ nghĩa (kiểu, đơn vị, khối, manifest) với bộ test file đúng và sai.
5. Làm **thông dịch khối FORM** (hàm thuần, kết quả như mục 7.3), nối vào endpoint đánh giá form của API.
6. Làm mô phỏng Host rồi Inventor Host cho P0, và builder `Tank`, `Cover` kèm manifest.

---

## 19. Giá trị đi kèm của CADFlow (export/import hai chiều)

Khi web CADFlow export một đơn hàng, file `.gea` gồm **script đã chạy** và, ở cuối file, một khối **chú thích** ghi lại giá trị đã đặt. Đây không phải cú pháp mới của ngôn ngữ: mỗi dòng của khối bắt đầu bằng `!@`, mà `!` đã là chú thích (mục 2), nên lexer, parser và add-in bỏ qua. File vẫn mở và chạy được ở mọi nơi dùng được `.gea`.

```
! ---------------------------------------------------------------------------
! CADFlow export: job AJ-20261108-112028-7e4c, product transformer
! Ordinary .gea tools ignore the lines below; CADFlow reads them back on import.
!@CADFLOW 1
!@JOB AJ-20261108-112028-7e4c
!@PRODUCT transformer 1
!@SCRIPT_SHA256 72d2ef08...
!@VALUE TR_TankLength 2400
!@VALUE TR_RadiatorCount 4
```

| Dòng | Ý nghĩa |
|------|---------|
| `!@CADFLOW 1` | Dấu nhận biết và phiên bản của khối. Không có dòng này thì file bị coi là `.gea` thường, không có giá trị đi kèm |
| `!@JOB <id>` | Job đã tạo file (chỉ để tham khảo) |
| `!@PRODUCT <mã> <phiên bản>` | Sản phẩm và phiên bản của nó trên server |
| `!@SCRIPT_SHA256 <hex>` | SHA-256 của script **không có khối này**, đã chuẩn hóa: xuống dòng LF, đúng một dấu xuống dòng ở cuối, UTF-8 không BOM. Dùng để biết giá trị có còn khớp với script không |
| `!@VALUE <tên> <số>` | Giá trị một thông số số của FORM, số thực dấu chấm, **theo đơn vị hiển thị của ô** (đúng đơn vị khai báo trong `USER_INPUT_PARAM`, ví dụ `mm`, `deg`). `INTEGER` là số nguyên. `CHECKBOX_PARAM` dùng `1`/`0` |
| `!@TEXT <tên> "<chuỗi>"` | Giá trị của ô `RADIOBUTTON_PARAM` hoặc ô nhập chuỗi. Chuỗi viết dạng JSON (thoát `\"`, `\\`, `\n`, `\uXXXX`) để nằm gọn trên một dòng; ký tự Unicode như tiếng Việt giữ nguyên |

Quy tắc:
- Khối luôn nằm cuối file. Export một file đã có khối thì khối cũ bị thay, không nhân đôi.
- Import đọc `!@VALUE` làm giá trị ban đầu của form thay cho giá trị mặc định ghi trong `USER_INPUT_PARAM`. Tên không có trong FORM của script thì bỏ qua và báo; giá trị vi phạm `MIN`/`MAX`/`ASSERT` hiện thành lỗi ở form như nhập tay.
- `SCRIPT_SHA256` không khớp (script đã bị sửa sau khi export) không phải lỗi: import vẫn chạy, kèm cảnh báo rằng giá trị có thể không còn hợp với script.
- Mã nguồn tham chiếu: `be/Cadflow.Api/GeaExport.cs` (`Build`, `ParseBlock`, `StripBlock`).

### 19.1. Chạy script được import trên web

- Server biên dịch lại script và tính FORM trước khi nhận đơn (không tin trình duyệt); worker làm lại một lần nữa khi dựng.
- Chỉ nhận `TARGET PART`, file tối đa 256 KB, tối đa 200 ô trên form.
- Mỗi lần chạy FORM hoặc BUILD tối đa **1.000.000 lệnh** (`GeaInterpreter.MaxSteps`, mã lỗi `GEA5001`), ngoài giới hạn 10.000 vòng cho mỗi `FOR`/`WHILE`. Nhờ vậy vòng lặp lồng nhau không treo server hay worker.
- Ngôn ngữ không có lệnh chạy chương trình ngoài hay đọc ghi file; BUILD chỉ gọi được builder trong danh mục (mục 12) trên Part mới của job.

---

## 20. `INCLUDE lib:` (component dùng lại)

Dùng lại một đoạn script (nhóm tham số trong FORM và các bước dựng trong BUILD) ở script khác mà không copy. Thư viện là file `.gea` trong `Scripts/lib/<tên>.gea` (ví dụ `tank-base-cover.gea`: nhóm "Tank, base and cover" của máy biến áp).

```
! Thư viện (Scripts/lib/tank-base-cover.gea)       ! Script dùng thư viện
GEA_VERSION 1                                      BEGIN_FORM
PRODUCT_NAME "Tank, base and cover"                  INCLUDE lib:tank-base-cover   ! chèn thân FORM của thư viện
PARAM_PREFIX TR_                                     BEGIN_GROUP "Radiators" ... END_GROUP
TARGET PART                                        END_FORM
BEGIN_FORM ... END_FORM                            BEGIN_BUILD
BEGIN_BUILD ... END_BUILD                            INCLUDE lib:tank-base-cover   ! chèn thân BUILD của thư viện
                                                     ... các bước dựng của script cha ...
                                                   END_BUILD
```

- `INCLUDE lib:<tên>` đứng **một mình trên dòng, bên trong** `BEGIN_FORM` hoặc `BEGIN_BUILD` (chú thích `!` ở cuối dòng được phép). Trong FORM nó chèn thân khối FORM của thư viện, trong BUILD chèn thân khối BUILD. Thư viện không có khối tương ứng thì chèn không gì cả. Vị trí và thứ tự do script cha quyết định.
- **Dùng chung biến** (quyết định Q1/D6): nội dung được chèn thẳng vào script cha, nên hai bên thấy và đặt được mọi biến và thông số. Khai báo trùng tên là lỗi `GEA2003`. Thư viện nên khai báo biến của mình và ghi trong chú thích đầu file những biến script cha được dùng.
- **Tiền tố**: header của thư viện không được chèn, nhưng `PARAM_PREFIX` của nó được thêm vào danh sách tiền tố hợp lệ của script cha. `PARAM_PREFIX` nhận một hoặc nhiều tiền tố (`PARAM_PREFIX GT_ TR_`); thông số hợp lệ nếu bắt đầu bằng một trong số đó.
- **Mở rộng trước lexer**: một bước tiền xử lý (`GeaIncludes.Expand`) thay các dòng INCLUDE bằng nội dung thư viện, trước khi lexer chạy. Bản đã mở rộng là script bình thường (không còn INCLUDE, dòng `PARAM_PREFIX` đã liệt kê cả tiền tố thư viện) nên tự biên dịch lại được.
- **Chẩn đoán**: số dòng luôn là dòng của file gốc; lỗi nằm trong thư viện có thông báo bắt đầu bằng `<tên>.gea:` và số dòng trong thư viện. Mã mới: `GEA1010` INCLUDE ngoài khối; `GEA1011` tên thư viện không hợp lệ (chỉ `A-Z a-z 0-9 _ -`, tối đa 64 ký tự, không đường dẫn); `GEA1012` không tìm thấy thư viện hoặc không có nơi nạp; `GEA1013` lồng quá 8 tầng; `GEA1014` vòng lặp; `GEA1015` cùng một thư viện được INCLUDE hai lần trong cùng loại khối.
- Giới hạn của bộ mở rộng: xử lý theo dòng, nên không nhận biết chú thích khối `/* ... */` trải nhiều dòng chứa `INCLUDE`.

### 20.1. Nơi nạp thư viện

| Nơi | Cách tìm |
|-----|----------|
| Server (import, tạo job) | `Scripts/lib/<tên>.gea` cạnh file chạy (`GeaLibraries`). Server mở rộng **trước khi lưu**: `ScriptStore` lưu và worker nhận bản đã mở rộng nên worker không cần thư viện, và **Export .gea** trả bản đã mở rộng (không còn dòng INCLUDE). |
| Add-in Inventor (Open .gea) | `<thư mục file>/lib/<tên>.gea`, rồi `<thư mục file>/<tên>.gea`, rồi `Scripts/lib` cạnh add-in (`GeaLibraryResolver`). |

Thêm thư viện mới: đặt `Scripts/lib/<tên>.gea` (cùng header như script thường, khối FORM/BUILD tùy chọn). Thư viện đi kèm server và add-in nhờ cấu hình ở `Cadflow.Api.csproj` và `InventorAddin.Core.csproj`.

Mã nguồn: `InventorAddin.Core/Gea/GeaIncludes.cs`, `GeaLibraryResolver.cs`; `GeaScript.Compile(text, resolveLib, fileName)`.

### 20.2. Quy ước chú thích cho thư viện

File thư viện được người dùng tải về để đọc, nên phải tự giải thích:

- Đoạn đầu của chú thích đầu file (đến dòng `!` trống) là mô tả ngắn, hiện trên thẻ ở trang Components.
- Mỗi **đoạn lệnh** (các dòng liên tiếp, không có dòng trống xen giữa) trong khối `BEGIN_FORM` và `BEGIN_BUILD` có ít nhất một dòng chú thích nói bước đó làm gì (ý nghĩa, công thức, vì sao).
- Chú thích bằng tiếng Việt như các script hiện có.

Test `ComponentCommentsTests` áp quy ước này cho mọi file trong `Scripts/lib`, nên thư viện thêm sau cũng phải giải thích.

### 20.3. Danh mục component trên web

Tab **Components** (`#/components`) liệt kê các thư viện trong `Scripts/lib` (thiết kế ở `docs/superpowers/specs/2026-11-10-component-catalog-design.md`). Mỗi thẻ có mô tả, biến dùng chung, bảng tham số và ba nút:

| Nút | Làm gì |
|-----|--------|
| Order this component | Lấy script khởi tạo, gửi qua `POST /api/gea/import` và mở form đặt hàng để dựng riêng component thành một Part |
| Download starter script (.gea) | Tải khung script có hai dòng `INCLUDE`, chú thích từng bước, chỗ thêm tham số và bước dựng riêng; sửa rồi Import lại |
| Download library (.gea) | Tải đúng file thư viện, có giải thích từng bước |

API (chỉ đọc): `GET /api/components`; `GET /api/components/{name}/gea`; `GET /api/components/{name}/starter?productName=&prefix=` (mặc định `New product` và `MY_`; tên không hợp lệ hoặc tiền tố sai trả 400, thư viện không có trả 404). Mã nguồn: `be/Cadflow.Api/Components.cs` (`ComponentCatalog`, `ComponentStarter`), `fe/src/pages/Components.jsx`.
