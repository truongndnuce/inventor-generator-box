# Mapping: ngôn ngữ kiểu SmartAssembly → ngôn ngữ riêng chạy trên Inventor và web

Tài liệu này dựa trên bảng mô tả ngôn ngữ SmartAssembly mà bạn cung cấp (mục 4.1 đến 4.12). Mục đích: khi người viết sản phẩm **gõ lệnh theo phong cách SmartAssembly**, hệ thống hiểu và thực thi được trên **Inventor (worker)** và **web (form, xem trước, chạy thử)**.

Nối tiếp `inventor-addin-plan.md` (mục 5c, DSL) và `cadflow-order-to-model-plan.md` (mục 15, web).

---

> **Đã chốt đuôi file `.gea`.** Cú pháp chính thức, kiểu, đơn vị và hành vi từng lệnh nằm ở `gea-language-spec.md`; file mẫu là `tank-level1.gea`. Nếu ví dụ ở mục 15 của tài liệu này khác thì **đặc tả `.gea` là bản chuẩn**.

## 1. Nguyên tắc

1. **Giữ phong cách**: mệnh lệnh, mỗi dòng một lệnh, từ khóa viết hoa, không dùng cú pháp gọi hàm kiểu ngoặc đơn, có ba khối (giao diện, bảng, logic chính).
2. **Giữ ý tưởng, tự đặt tên khi cần**: những lệnh chung (IF, FOR, DECLARE_VARIABLE...) giữ tên tương tự để quen tay. Lệnh gắn với thực thể Creo (UDF, feature, trail file) được thay bằng khái niệm tương đương của Inventor và đặt tên riêng. Nên tự đặt tên từ khóa và tự viết tài liệu của mình thay vì chép nguyên tài liệu của Sigmaxim; nếu có ý định thương mại hóa thì nên xem xét thêm về điều khoản và nhãn hiệu.
3. **Script chỉ điều phối**: như bảng của bạn nhận xét, giá trị kỹ thuật nằm ở **thư viện thành phần** (UDF ở Creo). Với Inventor, thư viện đó là part, iPart, iFeature, assembly mẫu và các hàm dựng viết bằng C#. Không có thư viện thì ngôn ngữ ít giá trị (xem mục 7).
4. **Người đặt hàng không viết code**: người dùng ở web chỉ điền giá trị vào form. Chỉ người soạn sản phẩm (tin cậy) mới viết script.

---

## 2. Khác biệt nền tảng giữa SmartAssembly và hệ thống này

| Điểm | SmartAssembly | Hệ thống này | Hệ quả cho ngôn ngữ |
|------|---------------|--------------|----------------------|
| Nơi chạy | Trong Creo, trên model đang mở, người dùng ngồi trước máy | Trên worker (Inventor) không người trông; form ở web | Không có hộp thoại tương tác trên worker; mọi nhập liệu phải xong **trước** khi chạy |
| Giao diện | Khối GUI hiện trong Creo | Khối GUI trở thành **form web** | Phải định nghĩa khi nào form được tính lại |
| Chọn trực tiếp trong model (USER_SELECT) | Có | Không (người đặt hàng không thấy model) | Cần thay thế hoặc giới hạn ở chế độ tương tác |
| Thông báo (MESSAGE_BOX) | Hộp thoại | Web hoặc nhật ký job | Phải định nghĩa hành vi khi không có người |
| Tham chiếu thực thể | Kiểu dữ liệu hạng nhất, dễ mất hiệu lực sau regenerate | Cùng vấn đề trong Inventor | Nên tự phục hồi tham chiếu, giảm nhóm lỗi hay gặp nhất |
| Thư viện | UDF của công ty | Part, iPart, iFeature, hàm dựng C# | Cần kế hoạch xây thư viện |
| Chạy thử | Chạy trong Creo | Cần chạy thử **không cần Inventor** trên web | Cần phần chạy mô phỏng (mục 14) |

---

## 3. Cấu trúc file: ba khối

| Khối SmartAssembly | Bắt buộc | Vai trò trong hệ thống này | Đề xuất tên khối |
|--------------------|----------|-----------------------------|------------------|
| `BEGIN_GUI_DESCR ... END_GUI_DESCR` | Không | Mô tả **form web**: ô nhập, hộp chọn, hiện hoặc ẩn theo lựa chọn | `BEGIN_FORM ... END_FORM` |
| `BEGIN_TAB_DESCR ... END_TAB_DESCR` | Không | Cấu hình **bảng**: catalog, BOM, bảng giá | `BEGIN_TABLES ... END_TABLES` |
| `BEGIN_ASM_DESCR ... END_ASM_DESCR` | Có | **Logic dựng** chạy ở worker | `BEGIN_BUILD ... END_BUILD` |

Quy tắc:
- Chú thích: `!` một dòng, `/* ... */` nhiều dòng (giữ nguyên).
- Thụt lề bằng Tab chỉ để dễ đọc, không mang ý nghĩa cú pháp.
- Mã hóa file: **UTF-8** (không dùng windows-1252) để viết được tiếng Việt trong thông báo.
- Khai báo biến ở đầu khối, gom theo kiểu như khuyến nghị của bảng.
- Khối FORM chạy ở **web và server** (hiện ô, kiểm tra), khối BUILD chạy ở **worker**.

---

## 4. Kiểu dữ liệu và khai báo

| SmartAssembly | Đề xuất | Cách hiện thực |
|---------------|---------|----------------|
| `STRING`, `INTEGER`, `DOUBLE`, `BOOL` | Giữ nguyên | Kiểu .NET tương ứng |
| (không có) | **Thêm `LENGTH`, `ANGLE`** (số có đơn vị) | Kiểm tra đơn vị tĩnh (quyết định D4); tránh nhầm mm với cm (đơn vị nội bộ API là cm và radian) |
| `DECLARE_VARIABLE kiểu tên [giá trị]` | Giữ nguyên | |
| `REFERENCE` / `DECLARE_REFERENCE tên` | Giữ, nhưng trỏ tới đối tượng Inventor | Feature, body, face, edge, occurrence, work plane, parameter (xem mục 6) |
| `ARRAY` (`ADD_ARRAY_ELEM`, `GET_ARRAY_SIZE`, `CLEAR_ARRAY`) | Giữ nguyên | Danh sách .NET. Cần cho danh sách sứ, danh sách điểm đường ống |
| `MAP` (`DECLARE_MAP`, `.key`, `.value`) | Giữ nguyên | Từ điển, dùng cho bảng tra |
| `STRUCT` (`POINT`, `VECTOR`, `PDF_OPTION`) | Giữ `POINT`, `VECTOR`; đổi `PDF_OPTION` thành `EXPORT_OPTION` | Ánh xạ sang điểm, vector của Inventor và `NameValueMap` cho tùy chọn translator |
| `STOPWATCH` | Giữ nguyên | Đo thời gian từng bước, đưa vào nhật ký job |

---

## 5. Luồng điều khiển

| SmartAssembly | Đề xuất | Ghi chú |
|---------------|---------|---------|
| `IF / ELSE_IF / ELSE / END_IF` | Giữ nguyên | |
| `AND OR NOT == <> < <=` (và `>`, `>=`) | Giữ nguyên, thêm `>` và `>=` nếu thiếu | So sánh số có đơn vị phải cùng đơn vị hoặc đổi được |
| `FOR x REF ARRAY / REF MAP ... END_FOR` | Giữ nguyên | |
| `WHILE ... END_WHILE`, `BREAK`, `CONTINUE`, `i++`, `i--` | Giữ nguyên | Thêm **giới hạn số vòng lặp tối đa** để job không chạy vô hạn |
| `RETURN` | Giữ nguyên | Kết thúc sớm, job vẫn thành công nếu chưa có lỗi |
| Hàm tiện ích (đổi chuỗi và số, trị tuyệt đối, tìm chuỗi con) | Giữ, thêm `MIN`, `MAX`, `ROUND`, `SQRT`, `ABS` | Dùng trong biểu thức |
| Không xóa phần tử khi đang duyệt mảng | **Chặn bằng trình thông dịch**: duyệt trên bản sao hoặc báo lỗi rõ ràng | Hệ thống tự xử lý, không để người viết gặp lỗi khó hiểu |

---

## 6. Mô hình tham chiếu (phần khó nhất)

| SmartAssembly | Tương đương trong Inventor | Đề xuất |
|---------------|-----------------------------|---------|
| `SEARCH_MDL_REF` | Tìm một đối tượng trong model | Giữ tên. Tìm theo **loại** (body, feature, face, edge, work plane, occurrence), **tên có ký tự đại diện**, hoặc **thuộc tính gắn** |
| `SEARCH_MDL_REFS` | Tìm nhiều đối tượng vào mảng | Giữ tên |
| `SET_FEAT_PARAM`, `SET_REF_PARAM` (gắn tham số lên feature hoặc cạnh để tìm lại) | **AttributeSets** gắn lên feature, face, edge, body (lưu trong file, còn sau khi mở lại) | Giữ ý tưởng, đổi tên thành `SET_REF_ATTR`; tìm bằng `SEARCH_MDL_REFS ... ATTR` |
| `REF_VALID`, `REF_EQUAL`, `IS_FAILED`, `PARAM_VALID` | Kiểm tra đối tượng còn hợp lệ, so sánh hai đối tượng, kiểm tra lệnh vừa chạy có lỗi | Giữ tên |
| `INVALIDATE_REF` rồi tìm lại sau regenerate | Đối tượng có thể hết hiệu lực sau khi model cập nhật | **Tự động hóa**: lưu tham chiếu dạng khóa bền (reference key của Inventor) hoặc theo thuộc tính, tự gắn lại khi dùng; vẫn giữ `INVALIDATE_REF` cho ai muốn tự làm |
| `COPY_REF`, `SET_REF_NAME` (ký tự `*` để tạo tên duy nhất), `GET_REF_NAME` | Đổi tên feature, body, occurrence | Giữ tên và quy tắc `*` |

Ghi chú:
- Tên chính xác của các thành phần API (reference key, attribute sets) cần kiểm tra lại với tài liệu Inventor 2027 khi làm.
- **Tự phục hồi tham chiếu** là cải tiến lớn so với SmartAssembly, vì bảng của bạn nêu đây là nhóm lỗi thường gặp nhất. Đổi lại, nó thêm công việc cho trình thông dịch và có thể chậm hơn; có thể làm sau Cấp 2.
- Nên **gắn nhãn bằng thuộc tính ngay khi tạo** (vai trò: `TANK`, `COVER`, `BUSHING_1`...) thay vì tìm theo tên dễ đổi.

---

## 7. Dựng model: tương đương của UDF

SmartAssembly không vẽ từng feature mà gọi UDF có sẵn, truyền tham chiếu vào, lấy tham chiếu ra. Tương đương trong Inventor:

| Cách | Mô tả | Ưu | Nhược | Dùng khi |
|------|-------|-----|-------|----------|
| **iFeature** | Feature đóng gói, dùng lại (gần UDF nhất) | Kỹ sư tự soạn bằng giao diện Inventor, không cần code | Khó kiểm thử tự động, quản lý phiên bản tệp | Chi tiết nhỏ lặp lại (lỗ, gân, rãnh) |
| **iPart / iAssembly** | Họ chi tiết hoặc cụm theo bảng thông số | Có sẵn bảng biến thể | Số biến thể cố định theo bảng | Họ sứ xuyên, van, mặt bích theo cấp điện áp |
| **Part hoặc assembly mẫu có Parameter** | File mẫu, chèn rồi gán Parameter | Đơn giản, trực quan | Mỗi biến thể là một lần cập nhật file | Các thành phần lớn (bộ làm mát, bình dầu) |
| **Hàm dựng C# (builder)** | Mã dựng sketch, feature, assembly, gọi từ script | Kiểm thử và quản lý phiên bản tốt, không phụ thuộc file | Phải viết code cho từng thành phần | Thùng, nắp, chi tiết hình học đơn giản (Cấp 1-2); bắt đầu bằng cách này |

Đề xuất lệnh:

| SmartAssembly | Đề xuất | Ý nghĩa |
|---------------|---------|---------|
| `CREATE_UDF <đường dẫn> <model> [tùy chọn] <nhóm> ... END_CREATE_UDF` | `CREATE_FEATURE <nguồn> ... END_CREATE_FEATURE` (nguồn có thể là `lib:` iFeature, hoặc `builder:` hàm C#) | Tạo feature hoặc body |
| `UDF_REF "tên" <ref>` | `FEATURE_IN "tên" <ref>` | Truyền tham chiếu đầu vào |
| `UDF_EXP_REF <biến> FEATURE <chỉ số>` | `FEATURE_OUT <biến> "tên"` | Lấy tham chiếu đầu ra (theo **tên**, không theo chỉ số, để ít vỡ khi thư viện đổi) |
| `UNGROUP_FEATURES` | Không cần | Builder và iFeature trả về đối tượng đã có tên |
| `SET_REF_NAME` | Giữ | Đặt tên |
| `GET_FEATURE_DIMS`, `GET_DIM_VALUE`, `SET_DIM_VALUE`, `SET_DIM_SYMBOL` | `GET_PARAM`, `SET_PARAM` (theo **tên Parameter**), `SET_PARAM_EXPR` | Dùng ModelParameters và UserParameters; điều khiển theo tên có đơn vị, không theo chỉ số kích thước |
| `ASSEMBLE` với ràng buộc | `ADD_COMPONENT <nguồn> <biến>` rồi khối `ASSEMBLE ... END_ASSEMBLE` với `MATE`, `FLUSH`, `INSERT`, `ANGLE` | Chèn occurrence và gán constraint |
| `REGEN_MDL` | `REGEN_MDL` | Cập nhật model (gọi lệnh cập nhật của document) |

**Kế hoạch xây thư viện** (nếu không có thư viện thì ngôn ngữ không có gì để điều phối):
- Cấp 1-2: hàm dựng C# cho thùng, nắp, gân, nắp kiểm tra (đã có thiết kế ở add-in).
- Cấp 3: họ sứ xuyên bằng iPart hoặc builder tham số hóa.
- Cấp 4-5: ống, van, bộ làm mát bằng assembly mẫu và builder cho đường ống.
- Mỗi thành phần có **tên, phiên bản, danh sách thông số vào và đối tượng ra** được ghi lại, và ghi phiên bản vào mỗi job.

---

## 8. Khối GUI sang form web

| SmartAssembly | Form web | Ghi chú |
|---------------|----------|---------|
| `CHECKBOX_PARAM` | Ô đánh dấu | Cờ "done" để chia nhiều giai đoạn tương ứng với **form nhiều bước (wizard)** |
| `RADIOBUTTON_PARAM` | Nhóm nút chọn một (hoặc hộp chọn) | |
| `USER_INPUT_PARAM` (`REQUIRED`, `TOOLTIP`) | Ô nhập số hoặc chữ, bắt buộc, gợi ý | Thêm đơn vị, `MIN`, `MAX`, mặc định |
| `USER_SELECT`, `USER_SELECT_MULTIPLE` (chọn cạnh, mặt, feature trong model) | **Không hỗ trợ trên web đặt hàng.** Thay bằng: chọn theo **tên hoặc vai trò** từ danh sách có sẵn; hoặc chỉ cho phép ở **chế độ tương tác trong add-in** (dùng chọn đối tượng của Inventor) | Là chỗ khác biệt lớn nhất với SmartAssembly |
| `MESSAGE_BOX`, `MESSAGE_BOX_EX` | Trên web: thông báo hoặc hộp xác nhận trong form. Trên worker: **ghi nhật ký job**; bản hỏi sẽ lấy **giá trị mặc định** đã khai báo, không chờ | Phải khai báo giá trị mặc định cho mọi hộp hỏi để job không bị treo |
| `PROCESSING_BOX_START/SET_STATE/END` | **Tiến trình job** (các bước hiện trên trang job như trong video: đang dựng assembly...) | Đặt tên bước để hiển thị |
| `IF` trong khối GUI (hiện hoặc ẩn ô) | Hiện hoặc ẩn ô trên web | |
| (thêm) | `ASSERT <điều kiện> "thông báo"` | Luật kiểm tra giá trị, thông báo hiện trên web và dừng gửi đơn nếu sai (thay cho kiểu "IF rồi MESSAGE_BOX rồi RETURN") |
| (thêm) | `OPTIONS`, `LOOKUP bảng` | Danh sách lựa chọn lấy từ bảng catalog, kèm quan hệ giữa lựa chọn (công suất và cấp điện áp hợp lệ) |

**Khi nào khối FORM được tính lại?** Bảng của bạn ghi đây chỉ là suy luận từ mẫu code. Vì đây là ngôn ngữ của bạn nên **tự định nghĩa**: khối FORM là **một hàm thuần được chạy lại mỗi khi bất kỳ giá trị nào đổi** (server hoặc WebAssembly). Mỗi lần chạy trả về: các ô hiện, các ô ẩn, giá trị hợp lệ, danh sách lỗi `ASSERT`, giá sơ bộ. Cách này cho hành vi dự đoán được và khớp với trải nghiệm trên web.

---

## 9. Mô-đun hóa và đường dẫn

| SmartAssembly | Đề xuất |
|---------------|---------|
| `INCLUDE lib:Ten_File.tab` | Giữ lệnh `INCLUDE lib:Ten_File` |
| Tiền tố `lib:` phân giải qua cấu hình (`SA_Paths.txt`), không dùng đường dẫn cứng | Phân giải theo **kho thư viện của phiên bản sản phẩm** (cấu hình trên server, mỗi worker có bản sao đồng bộ); cấm đường dẫn cứng |
| Module dùng chung biến của file cha (phạm vi không cô lập) | **Quyết định cần chốt.** Giữ như SmartAssembly cho đơn giản, hay cô lập mặc định và truyền tham số rõ ràng (ít lỗi hơn khi thư viện lớn dần) |

---

## 10. Xử lý lỗi và gỡ lỗi

| SmartAssembly | Đề xuất |
|---------------|---------|
| `BEGIN_CATCH_ERROR ... END_CATCH_ERROR`, cờ `ERROR`, `CLEAR_CATCH_ERROR` | Giữ lệnh. Cải tiến: **khi vào khối catch thì cờ tự xóa**, tránh dương tính giả; ngoại lệ từ Inventor (COM) được chuyển thành cờ `ERROR` kèm nội dung |
| `FIX_FAIL_UDF` | `FIX_FAIL_FEATURE` (thử sửa feature lỗi), dùng sau, chưa cần ở Cấp 1 |
| Mẫu "thử UDF trái, lỗi thì thử UDF phải" | Giữ, dùng khối catch |
| Trail file Creo (đường dẫn file, số dòng, thông báo) | **Nhật ký job**: tên file script, **dòng, cột**, lệnh, thông báo, thời gian; lưu theo job và hiện trên web |
| Tô sáng độ phủ dòng đã chạy (extension VS Code, `DEBUG_TAB_PERFORMANCE`) | Trình thông dịch ghi **vết thực thi** (dòng nào đã chạy); trang job và Product Studio tô sáng dòng đã chạy |
| (thêm) | `PRINT`, mức nhật ký (info, cảnh báo, lỗi), `STOPWATCH` cho từng bước |

---

## 11. Tích hợp ra ngoài

| SmartAssembly | Đề xuất cho hệ thống này | Ghi chú |
|---------------|--------------------------|---------|
| `EXCEL_*` (mở, đọc, ghi, đóng, `EXCEL_DISCONNECT`) | Đọc và ghi **file xlsx bằng thư viện** (không điều khiển ứng dụng Excel); lệnh `TABLE_LOAD`, `TABLE_GET`, `TABLE_SET` | Không để lại tiến trình Excel, không cần cài Excel trên worker |
| `JSON_LOAD_DOCUMENT`, `JSON_GET_VALUE`, `JSON_SET_VALUE` | Giữ | Dùng cho đầu vào của đơn và trao đổi với API |
| `READ_FEATURE_XML`, `MODIFY_FEATURE_XML` | Không cần | Đặc thù Creo |
| Bản vẽ: `CREATE_DRW_VIEW_GENERAL`, `CREATE_DRW_NOTE`, `SET_DRW_*`, `EXPORT_DRW_PDF` | `DRW_NEW`, `DRW_ADD_VIEW`, `DRW_ADD_SECTION`, `DRW_ADD_BALLOONS`, `DRW_ADD_PARTSLIST`, `DRW_ADD_NOTE`, `DRW_SET_*`, `EXPORT_PDF`, `EXPORT_STEP`, `EXPORT_IMAGE` | Ánh xạ sang các thành phần của tài liệu bản vẽ Inventor và translator xuất PDF, STEP. Đây là phần dễ vỡ nhất (xem rủi ro) |
| `FILE_OPEN/READ_LINE/WRITE_LINE/CLOSE`, `DELETE_FILE`, `WAIT_FOR_FILE` | Giữ nhưng chỉ cho phép trong **thư mục làm việc của job** | Chặn ghi ra ngoài thư mục job |
| Chạy `.exe`, VB, Java, C++, macro Excel, PowerShell qua COM | **Mặc định cấm** | Script nằm trên worker có quyền cao; chỉ bật cho script tin cậy, có danh sách cho phép |

**Về an toàn:** vì script chạy trên máy worker có quyền lớn, nguyên tắc là **người đặt hàng chỉ cung cấp giá trị, không cung cấp script**. Script chỉ do người soạn sản phẩm đã đăng nhập và có quyền xuất bản. Phiên bản đã xuất bản không sửa được.

---

## 12. Phân tích hình học

| SmartAssembly | Tương đương Inventor | Ưu tiên |
|---------------|----------------------|---------|
| `GET_SURFACE_NORM` | Lấy pháp tuyến mặt từ bộ tính của face | Thấp (Cấp 4 trở đi) |
| `VECTOR_NORMALIZE`, `VECTOR_FROM_POINTS` | Phép tính vector | Thấp |
| `MEASURE_DISTANCE` | Đo khoảng cách nhỏ nhất giữa hai đối tượng | Thấp |
| `GET_SURFACE_CONTOUR` | Đường bao của mặt (các vòng cạnh) | Thấp |
| `GET_REF_VERTEX`, `GET_REF_POS` | Đỉnh đầu, đỉnh cuối cạnh, vị trí | Thấp |

Với các cấp đầu (Part, thùng và nắp) **hầu như không cần** nhóm này, vì kích thước đến từ thông số, không đến từ phân tích mặt cạnh. Cần khi đặt ống theo mặt, tìm hai mặt đối nhau để đặt mối nối. Tên API cụ thể kiểm tra khi làm.

---

## 13. Bảng nhóm lệnh tổng hợp và mức ưu tiên

Ưu tiên theo các cấp của đối tượng (mục 5d của `inventor-addin-plan.md`): **P0** cần cho Cấp 1 (thùng và nắp), **P1** Cấp 2, **P2** Cấp 3-4 (sứ, ống, assembly), **P3** Cấp 5 và mở rộng.

| Nhóm | Lệnh | Ưu tiên |
|------|------|---------|
| Khai báo | `DECLARE_VARIABLE`, `DECLARE_REFERENCE`, `DECLARE_ARRAY`, `DECLARE_MAP`, `DECLARE_STRUCT` | P0 (biến), P1 (mảng), P2 (map, struct) |
| Luồng | `IF`, `FOR`, `WHILE`, `BREAK`, `CONTINUE`, `RETURN` | P0 |
| Biểu thức | Số học, so sánh, `MIN/MAX/ABS/ROUND`, đơn vị | P0 |
| Thông số | `SET_PARAM`, `GET_PARAM`, `SET_PARAM_EXPR` | P0 |
| Dựng | `CREATE_FEATURE` (builder), `SET_REF_NAME`, `REGEN_MDL` | P0 |
| Gắn nhãn | `SET_REF_ATTR` | P0 |
| Tìm | `SEARCH_MDL_REF`, `SEARCH_MDL_REFS`, `REF_VALID`, `INVALIDATE_REF` | P1 (theo tên, thuộc tính), P2 (theo mặt, cạnh) |
| Lỗi | `BEGIN_CATCH_ERROR`, `CLEAR_CATCH_ERROR`, `ERROR` | P0 |
| Giao diện | `USER_INPUT_PARAM`, `CHECKBOX_PARAM`, `RADIOBUTTON_PARAM`, `ASSERT`, `IF` trong FORM | P0 |
| Tiến trình | `PROCESSING_BOX_*` (thành bước job), `PRINT` | P0 |
| Thông báo | `MESSAGE_BOX*` | P1 |
| Chọn trong model | `USER_SELECT*` (chỉ chế độ tương tác) | P3 |
| Lắp ráp | `ADD_COMPONENT`, `ASSEMBLE` (`MATE`, `FLUSH`, `INSERT`, `ANGLE`) | P2 |
| Bảng, dữ liệu | `TABLE_*`, `JSON_*`, `LOOKUP` | P1 (catalog, giá), P2 |
| Bản vẽ, xuất file | `DRW_*`, `EXPORT_PDF`, `EXPORT_STEP`, `EXPORT_IMAGE` | P2 (ảnh, STEP), P2 (bản vẽ) |
| Hình học | `GET_SURFACE_NORM`, `MEASURE_DISTANCE`... | P3 |
| Module | `INCLUDE` | P1 |
| Tiện ích | `STOPWATCH`, `FILE_*` (trong thư mục job) | P2 |

---

## 14. Kiến trúc thông dịch

Khác với kế hoạch trước (tạo "kế hoạch dựng" trừu tượng rồi mới thực thi), ngôn ngữ kiểu SmartAssembly là **mệnh lệnh**: từng dòng chạy và có thể phụ thuộc kết quả dòng trước (ví dụ tìm tham chiếu rồi mới dựng tiếp). Vì vậy:

### 14.1. Đường ống
1. **Lexer** theo dòng: nhận từ khóa viết hoa, hằng, định danh, ghi dòng và cột.
2. **Parser** theo lệnh: mỗi lệnh có hình dạng tham số cố định (bảng lệnh khai báo số tham số, kiểu, tham số tùy chọn); khối lồng nhau (`IF/END_IF`, `FOR/END_FOR`, `CREATE_FEATURE/END_CREATE_FEATURE`).
3. **Phân tích ngữ nghĩa**: tên chưa khai báo, kiểu và đơn vị, `END_*` thiếu, lệnh không cho phép trong khối đó (ví dụ lệnh dựng trong khối FORM).
4. **Thông dịch (interpreter)** chạy cây lệnh và gọi **giao diện máy chủ (Host)** cho mọi việc đụng tới model.

### 14.2. Giao diện Host và hai bộ thực thi
| Bộ thực thi | Dùng ở đâu | Làm gì |
|-------------|-----------|--------|
| **Inventor Host** | Worker (và add-in) | Gọi Inventor API thật: dựng, đặt Parameter, tìm tham chiếu, bản vẽ, xuất file |
| **Mô phỏng Host (virtual model)** | Server và trình duyệt, test | Giữ một model **trong bộ nhớ**: Parameter, danh sách thành phần và vị trí, nhãn, cờ, BOM, giá, khối lượng ước tính; ghi lại **vết lệnh** |

Mô phỏng Host chính là ý tưởng "mô hình chủ ảo": cho phép **chạy thử trên web mà không cần Inventor**, cho ra BOM, giá, danh sách lỗi `ASSERT`, xem trước sơ đồ (từ vết lệnh) và test tự động.

**Giới hạn cần nói rõ**: mô phỏng không thể tái tạo hình học thật. Các lệnh phụ thuộc mặt, cạnh (`SEARCH_MDL_REFS` theo mặt, đo khoảng cách) chỉ chạy được ở Inventor Host; ở mô phỏng chúng trả giá trị giả hoặc bị bỏ qua và **đánh dấu "không mô phỏng được"** trong kết quả chạy thử. Vì vậy chạy thử trên web bảo đảm phần **logic, luật, giá, BOM**, còn đúng sai hình học vẫn cần chạy thử trên worker.

### 14.3. Hệ quả cho kế hoạch DSL
- Bước "kế hoạch dựng" ở mục 5c được thay bằng **giao diện Host và vết lệnh**. Phần còn lại của mục 5c (lexer, parser, ngữ nghĩa, đơn vị, thông báo lỗi có dòng cột, lộ trình D0-D5) vẫn dùng được.
- Canvas xem trước trên web đọc **vết lệnh của mô phỏng Host**, giữ nguyên ý tưởng "cùng một định nghĩa cho form, kiểm tra và xem trước".
- Adapter vẫn chỉ nhận một đối tượng `Inventor.Application` và là bên hiện thực **Inventor Host**.

---

## 15. Ví dụ minh họa cú pháp đề xuất

Ví dụ này chỉ minh họa phong cách và cách ánh xạ, **chưa chốt tên lệnh**. Đối tượng là Cấp 1 (thùng và nắp).

```
! May bien ap - Cap 1: thung va nap
BEGIN_FORM
  USER_INPUT_PARAM TR_TankLength LENGTH 100 mm MIN 40 MAX 500 REQUIRED TOOLTIP "Chieu dai ngoai thung"
  USER_INPUT_PARAM TR_TankWidth  LENGTH 60 mm  MIN 30 MAX 300 REQUIRED
  USER_INPUT_PARAM TR_TankHeight LENGTH 40 mm  MIN 20 MAX 200 REQUIRED
  USER_INPUT_PARAM TR_Wall       LENGTH 2 mm   MIN 1 MAX 10 REQUIRED
  CHECKBOX_PARAM   TR_HasManhole TRUE
  IF TR_HasManhole
    USER_INPUT_PARAM TR_ManholeDiameter LENGTH 40 mm MIN 20 MAX 100
  END_IF
  RADIOBUTTON_PARAM TR_Paint "Grey" "Green"
  ASSERT TR_Wall < MIN(TR_TankLength, TR_TankWidth) / 2 "Do day thanh phai nho hon mot nua canh ngan"
END_FORM

BEGIN_BUILD
  DECLARE_VARIABLE LENGTH innerR
  DECLARE_REFERENCE tank
  DECLARE_REFERENCE cover

  PROCESSING_BOX_SET_STATE "Dung thung"
  CREATE_FEATURE builder:Tank tank
    FEATURE_IN "Length" TR_TankLength
    FEATURE_IN "Width"  TR_TankWidth
    FEATURE_IN "Height" TR_TankHeight
    FEATURE_IN "Wall"   TR_Wall
    FEATURE_OUT tank "Body"
  END_CREATE_FEATURE
  SET_REF_NAME tank "TR_Tank*"
  SET_REF_ATTR tank "ROLE" "TANK"

  IF TR_HasManhole
    CREATE_FEATURE builder:Manhole tank
      FEATURE_IN "Diameter" TR_ManholeDiameter
    END_CREATE_FEATURE
  END_IF

  PROCESSING_BOX_SET_STATE "Dung nap"
  BEGIN_CATCH_ERROR
    CREATE_FEATURE builder:Cover cover
      FEATURE_IN "Tank" tank
      FEATURE_OUT cover "Body"
    END_CREATE_FEATURE
  END_CATCH_ERROR
  IF ERROR
    PRINT "Khong dung duoc nap"
    RETURN
  END_IF
  SET_REF_ATTR cover "ROLE" "COVER"

  REGEN_MDL
  SET_APPEARANCE tank TR_Paint
END_BUILD
```

### Bảng ánh xạ từng dòng gõ

| Dòng người dùng gõ | Trình thông dịch hiểu | Inventor Host làm | Mô phỏng Host làm |
|--------------------|-----------------------|--------------------|--------------------|
| `USER_INPUT_PARAM ... LENGTH 100 mm MIN 40 MAX 500` | Khai báo thông số có đơn vị và giới hạn | Tạo User Parameter có tên, đơn vị mm | Thêm vào model ảo, kiểm tra khoảng giá trị |
| `IF TR_HasManhole` trong FORM | Điều kiện hiện ô | (không chạy ở Inventor) | Tính lại khi giá trị đổi, hiện hoặc ẩn ô trên web |
| `ASSERT ... "thông báo"` | Luật kiểm tra | Dừng job nếu sai | Trả danh sách lỗi cho form |
| `DECLARE_REFERENCE tank` | Khai báo tham chiếu | Ô lưu đối tượng Inventor | Ô lưu tham chiếu ảo |
| `CREATE_FEATURE builder:Tank ... END_CREATE_FEATURE` | Gọi hàm dựng với các đối số | Chạy hàm C# dựng body bằng Inventor API trong Transaction | Ghi vết "tạo thùng với các kích thước" và thêm khối ảo |
| `FEATURE_OUT tank "Body"` | Lấy đối tượng ra theo tên | Gán body vừa tạo vào biến | Gán khối ảo |
| `SET_REF_NAME tank "TR_Tank*"` | Đặt tên duy nhất | Đổi tên body, tự thêm hậu tố nếu trùng | Đặt tên ảo |
| `SET_REF_ATTR tank "ROLE" "TANK"` | Gắn nhãn | Gắn AttributeSet lên body | Ghi nhãn |
| `IF TR_HasManhole ... CREATE_FEATURE builder:Manhole` | Dựng có điều kiện | Chạy builder khi đúng | Ghi vết khi đúng |
| `BEGIN_CATCH_ERROR ... END_CATCH_ERROR` | Bắt lỗi | Ngoại lệ COM đặt cờ `ERROR` | Cờ lỗi nếu builder ảo báo lỗi |
| `PROCESSING_BOX_SET_STATE "..."` | Cập nhật tiến trình | Gửi bước lên server cho trang job | Ghi vào vết |
| `REGEN_MDL` | Cập nhật model | Cập nhật document Inventor | Không làm gì (có thể tính lại khối lượng ước tính) |
| `SET_APPEARANCE tank TR_Paint` | Đặt màu sơn | Gán Appearance cho body | Ghi vào vết để vẽ xem trước |

---

## 16. Các quyết định cần chốt (cập nhật D1-D8 ở mục 5c)

| # | Quyết định | Đề xuất |
|---|-----------|---------|
| D1 | Phong cách | **Đã chọn hướng:** mệnh lệnh theo dòng, từ khóa viết hoa, ba khối, kiểu SmartAssembly (thay cho phương án lai ở bản trước) |
| D2 | Cú pháp bên ngoài | Văn bản riêng theo mục 3-5; **đuôi file `.gea`** (đã chốt), chi tiết ở `gea-language-spec.md` |
| D3 | Parser | Viết tay theo dòng, bảng lệnh khai báo hình dạng tham số cho từng lệnh |
| D4 | Đơn vị | Có `LENGTH`, `ANGLE` với kiểm tra tĩnh |
| D5 | Tên tham chiếu | Dùng tên Parameter trực tiếp; nhãn thuộc tính cho thực thể |
| D6 | Phạm vi biến của module | Chọn giữa chung biến như SmartAssembly và cô lập (mục 9) |
| D7 | Phiên bản ngôn ngữ | Có dòng khai báo phiên bản ở đầu file |
| D8 | Thông báo lỗi | Dòng, cột, lệnh, đoạn trích; tiếng Việt |
| **D9 (mới)** | Khi nào FORM tính lại | Chạy lại mỗi khi bất kỳ giá trị nào đổi (mục 8) |
| **D10 (mới)** | Tham chiếu tự phục hồi | Làm sau Cấp 2 (mục 6) |
| **D11 (mới)** | Nguồn thành phần ở Cấp 1 | Hàm dựng C# trước, iFeature và iPart từ Cấp 3 (mục 7) |
| **D12 (mới)** | Chạy chương trình ngoài | Mặc định cấm (mục 11) |

---

## 17. Rủi ro

| Rủi ro | Cách giảm |
|--------|-----------|
| Ngôn ngữ phình to, không dùng được vì thiếu thư viện thành phần | Làm thư viện song song và theo từng cấp; Cấp 1 chỉ cần vài builder |
| Mô phỏng Host phải theo kịp mọi lệnh mới | Mỗi lệnh khi thêm phải nói rõ hành vi ở cả hai Host, hoặc đánh dấu "không mô phỏng được" |
| Chạy thử trên web cho kết quả khác Inventor | Chỉ hứa bảo đảm logic, luật, giá, BOM; có bộ so sánh đầu ra giữa hai Host trên cùng bộ giá trị mẫu |
| Tham chiếu mất hiệu lực sau regenerate | Gắn nhãn thuộc tính ngay khi tạo; làm tham chiếu tự phục hồi (D10) |
| Script chạy lệnh nguy hiểm trên worker | Người đặt hàng không viết script; xuất bản có kiểm soát; mặc định cấm chạy chương trình ngoài và ghi file ngoài thư mục job |
| Hộp thoại chờ người trong worker | Mọi hộp hỏi có giá trị mặc định; chạy Inventor ở chế độ không hộp thoại |
| Vòng lặp vô hạn | Giới hạn số vòng và thời gian tối đa mỗi job |
| Copy quá sát ngôn ngữ độc quyền | Tự đặt tên và tài liệu riêng; chỉ giữ ý tưởng cấu trúc; xem xét điều khoản nếu thương mại hóa |
| Tên API Inventor thay đổi ở 2027 | Kiểm tra từng thành phần API khi hiện thực, ghi chú trong bảng lệnh |

---

## 18. Việc làm ngay tiếp theo

1. Chốt D6 (phạm vi biến của module) và đuôi file; xác nhận D1 là mệnh lệnh kiểu SmartAssembly.
2. **D0 (viết tay):** hoàn thiện ví dụ ở mục 15 thành file thật cho Cấp 1, dùng làm thước đo cho cú pháp.
3. Viết **bảng lệnh P0** (số tham số, kiểu, hành vi ở Inventor Host và mô phỏng Host).
4. Làm lexer, parser và thông dịch cho P0 cùng **mô phỏng Host** (không cần Inventor), kèm test và vết lệnh.
5. Viết **Inventor Host** cho P0 và hàm dựng C# cho thùng, nắp (dùng lại phần Adapter của add-in).
6. Nối vào web: khối FORM sinh form, chạy thử bằng mô phỏng Host, gửi job cho worker.
