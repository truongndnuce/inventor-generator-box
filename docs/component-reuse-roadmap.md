# Lộ trình: dùng lại và xuất component

Trạng thái ngày 2026-11-10. `INCLUDE lib:` đã xong (xem `superpowers/specs/2026-11-10-gea-include-design.md` và `sa-docs/gea-language-spec.md` mục 20): script này dùng lại nhóm tham số và các bước dựng của thư viện `Scripts/lib/<tên>.gea` (hiện có `tank-base-cover`). Tài liệu này ghi ba hướng tiếp theo. **Đây là lộ trình, chưa phải thiết kế chi tiết**: mỗi hướng cần một bản thiết kế riêng (spec rồi kế hoạch) trước khi làm.

## Hiện trạng cần biết

- Job dựng **một Part** và xuất `<jobId>.ipt`, `<jobId>.stp`, các ảnh `.png` và `.pdf` (`InventorJobExecutor`). Base, Tank, Cover là ba solid body trong cùng Part, tên `TR_Base`, `TR_Tank`, `TR_Cover` (`SET_REF_NAME`), có thuộc tính `ROLE` = `BASE`/`TANK`/`COVER`.
- Nút **Export .gea** ở trang job và Dashboard xuất **cả script** của job kèm giá trị (`GeaExport`), bản đã mở rộng INCLUDE. Không có cách xuất riêng một component.
- Web không có màn hình liệt kê thư viện; muốn dùng thư viện phải tự viết dòng `INCLUDE`.
- `ADD_COMPONENT`, `BEGIN_ASSEMBLE`/`END_ASSEMBLE`, `MATE`, `INSERT`, `EXPORT_STEP` là lệnh **đã dành riêng (P2) nhưng chưa làm**. `TARGET ASSEMBLY` chỉ chạy thử, Inventor Host chưa hỗ trợ (`GEA2020` cảnh báo). Server chỉ nhận `TARGET PART`.
- Máy phát triển hiện tại **không có Inventor**: phần add-in chỉ viết được, chưa chạy được. Mọi hạng mục dưới đây có phần add-in phải được kiểm trên máy Windows có Inventor 2027.

## Hướng A: danh mục component trên web (tải file `.gea`)

**Mục tiêu**: xem các component đang có và tải file `.gea` của từng cái, để dùng trong script của mình.

- API: `GET /api/components` (liệt kê `Scripts/lib`: tên, mô tả, tiền tố, danh sách tham số của khối FORM, các biến mà script cha được dùng) và `GET /api/components/{name}/gea` (tải file thư viện gốc, còn `INCLUDE` lồng nhau thì giữ nguyên).
- Mô tả của component lấy từ `PRODUCT_NAME` và đoạn chú thích đầu file; tham số lấy bằng cách biên dịch thư viện (đã có `GeaScript`).
- Web: trang **Components** trong thanh điều hướng, mỗi component một thẻ (tên, mô tả, bảng tham số, nút **Download .gea**, ví dụ hai dòng `INCLUDE` để copy).
- Tùy chọn sau: tải kèm các thư viện lồng nhau dưới dạng một file zip.
- Rủi ro: gần như không có (chỉ đọc file). Không cần Inventor.
- Kiểm thử: `Cadflow.Api.Tests` (liệt kê đúng, tên không hợp lệ trả 404, không đọc ngoài `Scripts/lib`).

## Hướng B: xuất file CAD riêng cho từng component

**Mục tiêu**: một job (ví dụ máy biến áp) trả thêm `Tank.ipt`/`.stp`, `Base.ipt`/`.stp`, `Cover.ipt`/`.stp` riêng, để dùng ở nơi khác.

- Cách khai báo (cần chốt khi thiết kế): lệnh trong BUILD của thư viện, ví dụ `EXPORT_COMPONENT tbcBody "Tank"`, hoặc khai báo trong header thư viện danh sách body cần xuất. Lệnh này cũng cần để người viết script chọn component nào xuất.
- Thực thi (add-in): tạo Part mới chứa riêng body đó (derive hoặc copy body), lưu `.ipt`, xuất `.stp`; thêm vào danh sách artifact của job với `kind` mới (`component-ipt`, `component-step`) và tên có tiền tố component. Server và R2 đã nhận artifact theo `name`/`kind` nên ít phải đổi; trang job cần nhóm các file theo component.
- Host mô phỏng: ghi vết lệnh (để test được trên server như `GeaSimulationHost`).
- Tham số: component xuất ra là **kết quả của lần dựng này** (kích thước cố định theo job), không có form riêng.
- Câu hỏi mở: xuất mặc định hay phải bật bằng tham số; một body hay một nhóm body cho một file; tên file; có kèm ảnh riêng không; ảnh hưởng thời gian dựng (mỗi component thêm một lần lưu và xuất STEP).
- Rủi ro: add-in và Inventor API (copy body sang Part khác giữ nguyên vị trí hay đưa về gốc tọa độ). Không thử được ở máy này.

## Hướng C: chèn Part đã dựng vào assembly

**Mục tiêu**: một script dựng assembly, trong đó chèn các Part (ví dụ Tank, base và cover đã dựng) và đặt vị trí bằng mate.

- Ngôn ngữ: làm các lệnh P2 đã dành riêng: `TARGET ASSEMBLY`, `ADD_COMPONENT`, `BEGIN_ASSEMBLE`/`END_ASSEMBLE` với `MATE`, `FLUSH`, `INSERT`. Cần đặc tả chi tiết trong `sa-docs/gea-language-spec.md` (hiện mục 11 chỉ giữ chỗ) và bảng manifest.
- Nguồn Part: (1) một Part do chính script này dựng bằng `INCLUDE` rồi lưu riêng; (2) Part đã có từ job trước (`jobId` + tên component, nhờ hướng B); (3) file `.ipt` trong thư viện. Nên bắt đầu với (1) và (2).
- Host: Inventor Host cho `ASSEMBLY` (tạo `.iam`, `Occurrences.AddByComponentName`, ràng buộc mate), host mô phỏng cho test. Server hiện chỉ nhận `TARGET PART`: cần mở cho ASSEMBLY, tính lại giới hạn (kích thước, số lệnh) và cách job lấy Part đầu vào.
- Câu hỏi mở: định danh component trong assembly (đặt tên, thuộc tính); xử lý khi Part nguồn bị xóa theo chính sách lưu giữ file (`Retention:Days`); mate cho thân hộp chữ nhật có đủ không hay cần face selector; xuất assembly ra `.iam`/`.stp` và bản vẽ.
- Rủi ro: lớn nhất (ngôn ngữ, add-in, server, kiểm thử Inventor). Chỉ làm sau khi hướng B chạy ổn.

## Thứ tự đề xuất

1. **A** (nhỏ, độc lập, không cần Inventor): làm trước, cho người dùng thấy và tải được component.
2. **B** (trung bình): cần máy có Inventor; mở đường cho C vì tạo ra các Part riêng.
3. **C** (lớn): làm cuối, dựa trên B.

Mỗi hướng: viết spec trong `docs/superpowers/specs/`, kế hoạch trong `docs/superpowers/plans/`, test trên server (`Cadflow.Api.Tests`) trước, phần add-in kiểm thử trên máy có Inventor.
