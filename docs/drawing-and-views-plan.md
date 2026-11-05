# Kế hoạch: thư viện ảnh, bản vẽ 2D (PDF) và ghi kích thước mặt cắt

## Context
Hiện job chỉ trả 1 ảnh iso + .ipt + .stp (`InventorAddin/InventorAddin/Web/InventorJobExecutor.cs`, hàm `Run`, `TryCapturePicture`). Cần: (1) cho user xem nhiều ảnh sau khi build, (2) xuất bản vẽ 2D, (3) tự ghi kích thước (dim) trên các mặt cắt. Đã chốt với user: PDF, bố cục chuẩn (trước/trên/cạnh + iso + mặt cắt chính), dim tự động theo từng sản phẩm, thư viện nhiều ảnh.

**Bước cuối khi thực thi:** chép bản kế hoạch này thành `docs/drawing-and-views-plan.md` (user yêu cầu thêm vào `docs/`; đang ở plan mode nên chưa ghi được), và thêm 1 dòng tham chiếu trong README.

## Thiết kế

### 1. Ảnh nhiều góc (rẻ nhất, làm trước)
- `InventorJobExecutor`: tách `TryCapturePicture(path, orientation)`; chụp các góc iso, front, top, right → `ID_iso.png`, `ID_front.png`, `ID_top.png`, `ID_right.png`. Dùng lại logic Fit + GetExtents/SetExtents (lề 15%).
- Upload với `kind = "image"`; thêm tên góc vào tên file để web gom nhóm. Không đổi API server (`/artifacts` đã nhận kind tuỳ ý).
- `fe/app.js` `renderJob`: thay 1 `<img>` bằng thư viện: ảnh lớn + dải thumbnail, bấm để đổi/phóng to (lightbox đơn giản, vẫn dựng DOM bằng `h()`/textContent). Thêm CSS ở `fe/styles.css`.

### 2. Bản vẽ 2D → PDF
- File mới `Services/DrawingBuilder.cs` (cùng phong cách `TransformerBuilder`): nhận part đã lưu `.ipt`, tạo `DrawingDocument` bằng template từ `FileManager.GetTemplateFile(kDrawingDocumentObject, metric)`.
- Base view (front) rồi `AddProjectedView` cho top/right và 1 iso; chọn tỉ lệ để vừa khổ giấy từ kích thước bao (`RangeBox`), làm tròn về tỉ lệ chuẩn (1:5, 1:10, 1:20…).
- Xuất PDF qua TranslatorAddIn PDF (GUID `{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}`), cùng cách `TryExportStep` đang làm. Không lưu .idw mặc định (user chỉ chọn PDF); `kind = "pdf"`.
- Gọi trong `Run` sau khi lưu .ipt, khi part vẫn mở; đóng drawing trong `finally` giống `CloseQuietly`. Lỗi bản vẽ chỉ báo progress và đi tiếp (như STEP), không làm hỏng job.
- Web: card "Drawing" có `<iframe>`/`<embed>` xem PDF + link tải. Server `ContentTypeFor` đã có `.pdf`.

### 3. Mặt cắt và ghi kích thước tự động
- Mặt cắt: `DrawingViews.AddSectionView` trên view front (cắt dọc qua tâm) và view top (cắt ngang) cho cả hai sản phẩm; với transformer thêm cắt qua trục sứ/bình dầu nếu cần.
- Dim: mỗi sản phẩm có một "bộ dim" cố định, khai báo cạnh định nghĩa tham số (cùng file `BoxParams.cs`/`TransformerParams.cs` hoặc lớp `DimensionPlan` riêng) để server và add-in không lệch:
  - Box: dài/rộng/cao, độ dày thành, khe nắp.
  - Transformer: dài/rộng/cao thùng, bề dày đế/nắp, khoảng cách cánh tản nhiệt, đường kính + chiều dài sứ, đường kính + độ cao bình dầu.
- Cách đặt: dùng `DrawingCurve` của view + `sheet.CreateGeometryIntent(curve, point)` rồi `sheet.DrawingDimensions.GeneralDimensions.AddLinear/AddDiameter`; chọn cạnh theo toạ độ bao (so với `RangeBox` của model) thay vì theo thứ tự, để không phụ thuộc số lượng sứ/cánh.
- **Cần spike trước** (chưa chắc API): chữ ký chính xác của `AddSectionView` (cần sketch đường cắt), việc `DrawingCurve` có lộ cạnh đúng ở view cắt không, và phương án dự phòng `RetrieveDimensions`/dim từ sketch của model. Chạy thử bằng DevRunner (ngoài repo, `...\Temp\devrun`) trên Inventor thật trước khi viết code chính.
- Tránh dim chồng nhau: đặt dim theo offset tăng dần ngoài khung view.

### 4. Server / hợp đồng
- Không cần endpoint mới. Có thể thêm `kind = "pdf"` vào quy ước và tuỳ chọn `summary` (số view, số dim).
- Tùy chọn (giai đoạn sau): checkbox "Include drawing" trên form (`fe/app.js`) → thêm trường vào request; mặc định bật.

## File sẽ sửa/tạo
- tạo `InventorAddin/InventorAddin/Services/DrawingBuilder.cs`
- sửa `InventorAddin/InventorAddin/Web/InventorJobExecutor.cs`
- sửa `fe/app.js`, `fe/styles.css`
- sửa `README.md`; tạo `docs/drawing-and-views-plan.md`

## Kiểm chứng
1. Spike DevRunner: dựng box + transformer, tạo drawing, xuất PDF, mở xem bằng ảnh chụp/Read.
2. Build add-in (`dotnet build` solution), **khởi động lại Inventor** (DLL bị khoá khi đang chạy).
3. Đặt đơn box và transformer từ web, xác nhận: succeeded, thư viện ảnh có 4 ảnh không bị cắt, PDF xem được trong trang job, dim đúng giá trị so với tham số, mặt cắt hiện đúng.
4. Ca biên: số sứ 1 và 8, 6 cánh tản nhiệt, thùng nhỏ nhất/lớn nhất (tỉ lệ bản vẽ vẫn vừa khổ giấy, dim không chồng).
5. Lỗi bản vẽ không làm job thất bại (kiểm tra bằng cách ép lỗi).

## Rủi ro
- Placement dim tự động là phần khó nhất; có thể cần vài vòng chỉnh trên Inventor thật.
- Mỗi lần thử add-in cần restart Inventor (~4 phút); dùng DevRunner để lặp nhanh.

---

## Kết quả thực hiện (cập nhật sau spike trên Inventor 2027)

### Quy ước bản vẽ áp dụng (tiêu chuẩn cơ khí chung, ISO/TCVN)
| Hạng mục | Quy ước | Cách thực hiện |
|---|---|---|
| Phương pháp chiếu | ISO 128: **chiếu góc thứ nhất** (hình chiếu bằng ở **dưới** hình chiếu đứng, hình chiếu cạnh bên **phải**) | `AddProjectedView` đặt vị trí theo góc thứ nhất; template `ISO.idw` |
| Khổ giấy | ISO 5457 (A4..A0, nằm ngang) | `DrawingBuilder.ChooseSheet`: khổ nhỏ nhất vừa ở tỉ lệ không nhỏ hơn 1:20, không có thì lên khổ lớn hơn, tối đa A0 |
| Tỉ lệ | ISO 5455: 5:1, 2:1, 1:1, 1:2, 1:5, 1:10, 1:20, 1:50, 1:100, 1:200, 1:500, 1:1000 | Chọn tỉ lệ lớn nhất vẫn vừa khổ giấy; hình iso dùng nửa tỉ lệ |
| Nét vẽ, mũi tên, chiều cao chữ, kiểu dim | ISO 128/129 | Do template `ISO.idw` đảm nhiệm, code không ghi đè |
| Vị trí dim | ISO 129: dim ngoài hình, hàng đầu cách hình ≥ 10 mm, hàng sau cách hàng trước ≥ 8 mm; không ghi kích thước thừa | Offset 15 mm và 26 mm; mỗi kích thước bao chỉ ghi một lần |
| Đơn vị | mm, không ghi ký hiệu đơn vị | Template mét; dim hiện `2240,00` |
| Bố cục | Chừa lề đóng gáy 20 mm bên trái, không đè khung tên (góc dưới phải) | `LeftRoom`, `TopRoom`; hình iso ở khoảng trống phía trên khung tên |
| Mặt cắt | Đường cắt A-A qua tâm, nét gạch chấm, vẽ ký hiệu mũi tên, vạch gạch chéo | `AddSectionView` trên hình chiếu đứng |

Khi có tiêu chuẩn riêng của công ty (ví dụ template của GE Vernova), thay template trong `DrawingBuilder.Build` (`GetTemplateFile`) và các hằng số bố cục ở đầu file; phần còn lại giữ nguyên.

### Phát hiện từ spike (API Inventor, dễ sai)
- `GetTemplateFile(kDrawingDocumentObject, metric)` **không** cho template mét: trả `Standard.idw` (inch, khổ Letter). Phải truyền `DraftingStandardEnum.kISO_DraftingStandard` (file `Metric\ISO.idw`).
- Template ISO là chiếu góc thứ nhất: hình chiếu đặt phía trên hình đứng sẽ thành hình chiếu **dưới** (bottom view), không phải hình chiếu bằng.
- Sketch của view (đường cắt) dùng toạ độ **kích thước mô hình (chưa nhân tỉ lệ)**, gốc ở tâm view; sketch phải thêm vào `view.Sketches`, không phải `sheet.Sketches`. Đưa toạ độ theo cm trên giấy thì đường cắt chỉ dài bằng 1/tỉ lệ, mặt cắt chỉ ra một lát mỏng.
- Mặt cắt luôn căn thẳng hàng với view cha: tham số `Position` chỉ có tác dụng theo phương chiếu.
- `DrawingCurve.Segments[1]` là `DrawingCurveSegment` (có `StartPoint/EndPoint`), không ép về `LineSegment2d` được.
- Cạnh nằm ngang của vật thể tròn (sứ, bình dầu) hiện ra là vòng tròn nhìn nghiêng (RangeBox cao gần 0), không phải đoạn thẳng. Chọn cạnh ngoài cùng bằng `Evaluator2D.RangeBox` của mọi loại curve rồi đặt `GeometryIntent` bằng một điểm nằm trên curve.
- `DrawingView.DrawingCurves` có thể gồm cả sketch của đường cắt (nằm ngoài khung view): phải lọc.
- Mô hình dựng theo trục Z (hộp) cần hướng nhìn `kBottomViewOrientation` để có hình chiếu đứng đúng; mô hình dựng theo trục Y (máy biến áp) dùng `kFrontViewOrientation`.
- Tạo drawing làm đổi view đang active: chụp ảnh các góc **trước**, dựng bản vẽ **sau**.

### Triển khai
- `Services/DrawingBuilder.cs`: dựng bản vẽ + xuất PDF qua TranslatorAddIn (`{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}`).
- `Web/InventorJobExecutor.cs`: chụp 4 góc (iso, front, top, right) rồi gọi `DrawingBuilder`; lỗi bản vẽ chỉ cảnh báo, job vẫn thành công. Summary thêm `drawingViews`, `drawingDims`, `drawingScaleDenominator`.
- `fe/app.js`: thư viện ảnh (ảnh lớn + thumbnail) và khung xem PDF. Server trả file inline để trình duyệt hiển thị.
- Dim chuỗi trên mặt cắt: máy biến áp (đế, thân thùng, nắp); hộp (đáy, chiều cao) — mức hộp chưa kiểm chứng trên Inventor thật.

### Việc chưa làm / chưa kiểm chứng (cập nhật 2026-11-06)
- [ ] **Chưa chạy qua luồng web thật**: add-in mới chưa được nạp vào Inventor (DLL bị khoá, cần đóng và mở lại Inventor). Cần đặt đơn máy biến áp và hộp từ web, kiểm tra job succeeded, đủ file `.png` (iso/front/top/right) + `.pdf`.
- [ ] Thư viện ảnh nhiều góc và khung xem PDF trên web (`fe/app.js`, `fe/styles.css`) mới viết, chưa chạy trong trình duyệt.
- [ ] Bản vẽ của **hộp** chưa thử: mô hình dựng theo trục Z (hướng nhìn `kBottomViewOrientation`), chuỗi dim đáy/chiều cao chưa kiểm chứng; nếu không tìm thấy cạnh thì chuỗi dim bị bỏ qua, không báo lỗi.
- [ ] Dim trên mặt cắt mới có chuỗi chiều cao đế/thân/nắp. Chưa có dim đường kính + chiều dài sứ, đường kính bình dầu, khoảng cách cánh tản nhiệt (kế hoạch mục 3).
- [ ] Mặt cắt chỉ cắt dọc qua tâm hình chiếu đứng; chưa có mặt cắt ngang (trên hình chiếu bằng) hay mặt cắt qua trục sứ/bình dầu.
- [ ] Máy biến áp là khối đặc nên mặt cắt gạch chéo kín; chưa có vỏ rỗng/chi tiết bên trong.
- [ ] Chưa áp dụng tiêu chuẩn nội bộ của GE Vernova (không có tài liệu); đang dùng ISO chung. Khi có template `.idw` của họ: đổi `GetTemplateFile` và các hằng số bố cục trong `DrawingBuilder`.
- [ ] Hình iso đặt ở vị trí cố định (0.72 W, 0.3 H); chưa kiểm tra tránh chồng lên dim/mặt cắt ở mọi cỡ. Ca nhỏ nhất/lớn nhất mới kiểm tra "ra PDF", chưa xem kỹ từng bản vẽ.
- [ ] Chưa có tuỳ chọn "Include drawing" trên form; chưa lưu `.idw` gốc (chỉ PDF); chưa xuất DWG.
- [ ] Chưa kiểm thử đường lỗi (Inventor treo/hộp thoại) và nhiều worker.
- [ ] README chưa cập nhật cho sản phẩm máy biến áp, bản vẽ và DevRunner (công cụ thử nằm ngoài repo, `%TEMP%\devrun`).
