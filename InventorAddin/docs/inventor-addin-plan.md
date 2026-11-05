# Kế hoạch: Add-in Inventor tạo đối tượng và điều chỉnh thông số

## 1. Mục tiêu

Xây dựng một add-in cho Autodesk Inventor có khả năng:

1. **Tạo** một đối tượng (Part hoặc Assembly) theo mẫu định nghĩa sẵn.
2. **Điều chỉnh** thông số của đối tượng đó sau khi tạo, và model tự cập nhật.
3. **Nhận diện lại** đúng đối tượng do add-in tạo, kể cả sau khi lưu và mở lại file.

### Ngoài phạm vi (giai đoạn đầu)
- Chưa làm bản vẽ (Drawing).
- Chưa làm nhiều loại đối tượng cùng lúc. Chỉ làm 1 loại đối tượng mẫu để chốt kiến trúc.
- Chưa đóng gói installer.

---

## 2. Các quyết định cần chốt trước khi code

| # | Câu hỏi | Lựa chọn | Ghi chú |
|---|---------|----------|---------|
| 1 | Phiên bản Inventor mục tiêu | **Inventor 2027 (đã chốt)** | Target **.NET 10**, dùng Visual Studio 2026 và Inventor 2027 SDK/DeveloperTools.msi. Không dùng .NET Framework 4.8 |
| 2 | Loại đối tượng | **Part (đã chốt)** | Assembly để dành cho Giai đoạn 5 nếu cần |
| 3 | Hình dạng mẫu đầu tiên | **Hộp rỗng có nắp (đã chốt)** | Thân hộp và nắp là 2 solid body trong 1 Part. Thông số xem mục 4 |
| 4 | Danh sách thông số | Tên, kiểu, đơn vị, min/max, mặc định | Lập bảng ở mục 4 |
| 5 | Tạo và chỉnh ở đâu | **Trên model (Part) đang mở (đã chốt)** | Không tạo document mới. Add-in làm việc với document đang active. Quy tắc xử lý xem Giai đoạn 2 |
| 6 | UI | **Ribbon + Dockable window WPF, kèm canvas xem trước (đã chốt)** | Vừa xem model vừa chỉnh. Chi tiết ở mục 5b |
| 7 | Lưu preset | **File JSON (đã chốt, lưu tạm)** | File mẫu `presets.json` đi kèm kế hoạch. Chi tiết ở mục 5b |

---

## 3. Kiến trúc đề xuất

### 3.1. Phân lớp
- **AddIn Host**: lớp server của add-in, quản lý vòng đời, đăng ký ribbon, dọn tài nguyên khi tắt.
- **UI Layer (WPF)**: form nhập thông số, không chứa logic Inventor.
- **Application Layer**: các use case như `TạoĐốiTượng`, `CậpNhậtThôngSố`, `ĐọcThôngSố`.
- **Inventor Adapter**: lớp duy nhất gọi trực tiếp Inventor API. Các lớp khác chỉ làm việc với model của riêng mình.
- **Domain/Model**: định nghĩa đối tượng và thông số (tên, đơn vị, ràng buộc), độc lập với Inventor để dễ test.

### 3.2. Nguyên tắc thiết kế
- **Parametric trước**: mọi kích thước quan trọng gắn vào User Parameter có tên. Chỉnh bằng cách đổi giá trị Parameter, không xóa vẽ lại hình học.
- **Một nơi định nghĩa thông số**: tên, đơn vị, giới hạn nằm ở một bảng cấu hình, cả UI và logic đều đọc từ đó.
- **Mọi thao tác gói trong một Transaction** để Ctrl+Z hoàn tác trọn vẹn.
- **Chỉ chạy trên UI thread** của Inventor.

---

## 4. Bảng thông số (điền khi chốt hình dạng)

| Tên Parameter | Ý nghĩa | Kiểu | Đơn vị | Mặc định | Min | Max |
|---------------|---------|------|--------|----------|-----|-----|
| BX_Length | Chiều dài ngoài của hộp | Length | mm | 100 | 40 | 500 |
| BX_Width | Chiều rộng ngoài của hộp | Length | mm | 60 | 30 | 300 |
| BX_Height | Chiều cao thân hộp (chưa tính nắp) | Length | mm | 40 | 20 | 200 |
| BX_Wall | Độ dày thành | Length | mm | 2 | 1 | 10 |
| BX_Bottom | Độ dày đáy | Length | mm | 2 | 1 | 10 |
| BX_CornerRadius | Bán kính bo góc đứng (ngoài) | Length | mm | 3 | 0 | 20 |
| BX_LidThickness | Độ dày nắp | Length | mm | 2 | 1 | 10 |
| BX_LidLip | Chiều cao gờ nắp lồng vào trong hộp | Length | mm | 4 | 2 | 15 |
| BX_LidClearance | Khe hở giữa gờ nắp và thành trong | Length | mm | 0.3 | 0 | 1 |

#### Hình dạng đối tượng
- **Thân hộp**: Extrude hình chữ nhật bo góc (Length x Width, bo `BX_CornerRadius`), rồi Shell mở mặt trên với độ dày `BX_Wall`, đáy dày `BX_Bottom`.
- **Nắp**: tấm cùng kích thước ngoài, dày `BX_LidThickness`, phía dưới có gờ nhô xuống `BX_LidLip` lồng vừa vào lòng hộp (cách thành trong `BX_LidClearance`).
- **Cách chứa trong file**: thân và nắp là **hai solid body trong cùng một Part**, nắp đặt phía trên thân hộp, tách rời. Nếu sau này cần xuất nắp thành file riêng hoặc lắp ráp có chuyển động thì chuyển sang Assembly (Giai đoạn 5).

#### Ràng buộc hợp lệ (kiểm tra trên UI trước khi áp dụng)
- `BX_Wall` nhỏ hơn một nửa của `min(BX_Length, BX_Width)` và nhỏ hơn `BX_Height - BX_Bottom`.
- `BX_CornerRadius` không vượt quá một nửa của `min(BX_Length, BX_Width)`.
- Bán kính bo góc trong (`BX_CornerRadius - BX_Wall`) nếu âm thì lấy bằng 0, không để sketch bị lỗi.
  - **Đã điều chỉnh khi triển khai**: Fillet/Shell của Inventor lỗi khi bán kính bằng 0, và gờ nắp cần bo bán kính `BX_CornerRadius - BX_Wall - BX_LidClearance`. Nên bắt buộc `BX_CornerRadius > BX_Wall + BX_LidClearance` (mặc định 3 > 2.3). Nghĩa là giá trị min 0 của `BX_CornerRadius` ở bảng trên thực tế không dùng được.
- `BX_LidLip` nhỏ hơn `BX_Height - BX_Bottom` để gờ không chạm đáy.
- `BX_LidClearance` nhỏ hơn `BX_Wall` để gờ nắp không bị lệch ra ngoài thành.
- Các giới hạn min/max ở bảng chỉ là điểm khởi đầu, sẽ chỉnh lại sau khi test ở giá trị biên.

#### Mở rộng sau (chưa làm ở bản đầu)
- Gân tăng cứng bên trong đáy: thêm `BX_RibCount` (số nguyên, không đơn vị) điều khiển Pattern.
- Lỗ bắt vít ở bốn góc, rãnh hoặc lỗ thoát khí trên nắp.

Lưu ý:
- Đặt tên Parameter theo một quy ước thống nhất (ví dụ có tiền tố riêng của add-in) để không đụng tên với Parameter người dùng tự tạo.
- Luôn dùng expression có đơn vị khi gán. Đơn vị nội bộ của API là cm và radian, dễ gây sai số nếu gán số thuần.

---

## 5. Các giai đoạn thực hiện

### Giai đoạn 0: Chuẩn bị môi trường
- [ ] Cài Visual Studio 2026 (cần để build assembly .NET 10), Inventor 2027 SDK và DeveloperTools.msi (cài kèm template Add-in cho Visual Studio).
- [ ] Target framework: .NET 10. Tham khảo API Help "What's New" của Inventor 2027.
- [ ] Xem cách khai báo `.addin` và cách nạp thư viện phụ thuộc (NuGet): Inventor 2027 có cơ chế cô lập dependency (`UseInventorAssemblyContext`), nên đọc tài liệu trước khi thêm package.
- [ ] Không dùng `BinaryFormatter` (đã bị gỡ trong .NET mới). Nếu cần lưu preset thì dùng JSON.
- [ ] Add-in kiểu Regfree (không đăng ký registry), cài qua file `.addin` hoặc ApplicationPlugins bundle.
- [ ] Tạo repo, cấu trúc solution theo các lớp ở mục 3.
- **Hoàn thành khi**: add-in rỗng được Inventor nạp, hiện được một nút trên ribbon.

### Giai đoạn 1: Khung add-in
- [ ] Đăng ký ribbon tab, panel, button.
- [ ] Mở được dockable window chứa một UserControl WPF rỗng. Làm sớm vì đây là điểm dễ vướng nhất của UI (cách host WPF vào cửa sổ dock của Inventor, xử lý đổi theme sáng/tối, kích thước khi kéo giãn).
- [ ] Thiết lập log ra file (rất cần khi debug add-in).
- [ ] Quy trình debug: gắn debugger vào Inventor, kiểm tra việc nạp/gỡ add-in.
- **Hoàn thành khi**: bấm nút hiện được UI và ghi được log.

### Giai đoạn 2: Tạo đối tượng mẫu
- [ ] Lấy document đang active và kiểm tra điều kiện trước khi tạo:
  - Không có document nào mở: báo người dùng mở hoặc tạo một Part.
  - Document không phải Part (ví dụ Assembly, Drawing): báo lỗi rõ ràng, không làm gì.
  - Part đã có đối tượng của add-in (nhận diện bằng AttributeSet): không tạo lại, chuyển sang chế độ chỉnh thông số.
  - Part đã có hình học hoặc Parameter khác: cảnh báo và cho người dùng xác nhận trước khi thêm vào.
- [ ] Kiểm tra trùng tên: nếu Parameter có tiền tố `BX_` đã tồn tại mà không do add-in tạo, dừng và báo, không ghi đè.
- [ ] Đặt tên rõ cho sketch, feature và hai body (ví dụ thân hộp, nắp) để dễ nhận diện và dọn dẹp.
- [ ] Nếu tạo giữa chừng bị lỗi, hoàn tác toàn bộ (Transaction) để không để lại model dở dang.
- [ ] Tạo User Parameter từ bảng thông số.
- [ ] Tạo sketch, ràng buộc kích thước gắn vào Parameter, rồi tạo feature.
- [ ] Gắn dấu nhận diện (AttributeSet) vào document để biết đây là đối tượng của add-in.
- **Hoàn thành khi**: bấm nút là ra đúng model, mở bảng Parameters của Inventor thấy các thông số đúng tên và đơn vị.

### Giai đoạn 3: Điều chỉnh thông số
- [ ] Đọc danh sách thông số từ model đang mở và hiển thị lên UI, đồng thời vẽ canvas xem trước theo các giá trị đó.
- [ ] Sửa giá trị trên UI: canvas đổi ngay khi gõ, kiểm tra min/max và ràng buộc hợp lệ, bấm Áp dụng thì mới đẩy vào model và cập nhật.
- [ ] Xử lý lỗi khi giá trị làm sketch hoặc feature fail (hoàn tác và báo rõ lý do).
- [ ] Hỗ trợ Undo qua Transaction.
- **Hoàn thành khi**: đổi thông số trên UI thì model đổi theo; nhập giá trị sai thì không làm hỏng model.

### Giai đoạn 4: Độ bền và trải nghiệm
- [ ] Nhận diện lại đối tượng sau khi lưu và mở lại file.
- [ ] Xử lý các trường hợp: không có document đang mở, document không phải do add-in tạo, người dùng tự sửa Parameter bằng tay.
- [ ] Dọn tham chiếu COM, kiểm tra Inventor thoát sạch khi đóng.
- [ ] Preset: nạp, chọn, lưu, ghi đè, xóa preset từ file JSON (xem mục 5b), kèm xử lý file hỏng hoặc preset không hợp lệ.
- **Hoàn thành khi**: qua được bộ test thủ công ở mục 6.

### Giai đoạn 5 (tùy chọn): Mở rộng
- Thêm loại đối tượng thứ hai để kiểm chứng kiến trúc.
- Chuyển sang Assembly nếu đối tượng gồm nhiều chi tiết.
- Cho phép nhập thông số từ file (Excel/JSON).
- Đóng gói ApplicationPlugins bundle để cài đặt gọn.

---

## 5b. Thiết kế UI và canvas xem trước

### Bố cục dockable window
- **Phần trên: canvas xem trước** (xem bên dưới).
- **Phần giữa: form thông số**, chia hai nhóm "Thân hộp" (Length, Width, Height, Wall, Bottom, CornerRadius) và "Nắp" (LidThickness, LidLip, LidClearance). Mỗi thông số gồm nhãn, ô nhập số, đơn vị mm và slider theo khoảng min/max. Form dựng từ bảng thông số ở mục 4, không viết cứng từng ô.
- **Phần dưới: nút hành động**: Tạo (khi Part chưa có hộp), Áp dụng, Hoàn nguyên về giá trị đang có trong model, Mặc định. Thanh trạng thái hiện lỗi hoặc cảnh báo ràng buộc.

### Canvas xem trước
- Vẽ bằng WPF (`Canvas` với `Path`/`Line`/`TextBlock`), hai khung nhìn:
  - **Mặt bằng** (nhìn từ trên): hình chữ nhật bo góc ngoài, đường thành trong, nhãn Length và Width.
  - **Mặt cắt dọc**: thân hộp (thành và đáy), nắp phía trên với gờ lồng vào trong, khe hở giữa gờ và thành, nhãn Height, Wall, Bottom, LidThickness, LidLip, LidClearance.
- Tỷ lệ tự động co giãn cho vừa khung nhưng **giữ đúng tỷ lệ thật** giữa các kích thước, không bóp méo.
- Đường kích thước và nhãn giá trị cập nhật ngay khi người dùng gõ hoặc kéo slider.
- Khi ô nhập đang được focus thì **tô nổi bật kích thước tương ứng** trên canvas, giúp người dùng biết thông số này là cạnh nào.
- Thành và khe hở rất mỏng so với hộp nên vẽ với độ dày tối thiểu vài pixel, kèm chế độ phóng to vùng mặt cắt nếu cần.
- Giá trị không hợp lệ: ô nhập viền đỏ, canvas giữ hình hợp lệ gần nhất và đánh dấu kích thước lỗi, nút Áp dụng bị vô hiệu hóa.
- Hai khung có thể chuyển bằng tab hoặc hiện song song nếu cửa sổ đủ rộng.

### Nguyên tắc kỹ thuật
- Dùng **MVVM**: ViewModel giữ giá trị thông số, trạng thái hợp lệ và lệnh. View chỉ binding.
- **Lớp tính hình cho canvas là lớp thuần** (nhận bộ thông số, trả danh sách hình và nhãn), không gọi Inventor. Nhờ vậy xem trước mượt, không làm model tính lại theo từng phím, và test được mà không cần mở Inventor.
- **Canvas chỉ là sơ đồ minh họa**, không phải bản render chính xác của Inventor. Model thật chỉ thay đổi khi bấm Áp dụng (một Transaction).
- Mọi lời gọi Inventor API chỉ chạy trên UI thread của Inventor.
- Khi mở UI hoặc bấm Làm mới, đọc lại giá trị từ model. Nếu người dùng sửa Parameter trực tiếp trong Inventor, UI cập nhật theo ở lần làm mới tiếp theo.

### Preset (lưu bằng file JSON)
- **Preset** là một bộ giá trị thông số được đặt tên (ví dụ Hộp nhỏ, Hộp tiêu chuẩn, Hộp lớn). Trên UI là danh sách thả xuống: chọn preset thì các ô nhận giá trị và canvas vẽ lại ngay, bấm Áp dụng mới đổi model.
- File mẫu: `presets.json`, gồm 3 preset ban đầu. Tất cả đều thỏa các ràng buộc ở mục 4.
- **Cấu trúc file**: `schemaVersion`, `units` (mm), và danh sách `presets`, mỗi preset có `name` và `values` với khóa là tên Parameter (`BX_Length`, ...). Dùng chính tên Parameter làm khóa để không phải ánh xạ thêm.
- **Vị trí lưu**: file preset của người dùng nằm trong thư mục dữ liệu riêng của add-in (ví dụ dưới `%AppData%`), file mẫu đi kèm add-in chỉ đọc. Lần đầu chạy thì sao chép file mẫu sang thư mục người dùng.
- **Chức năng**: chọn preset, "Lưu thành preset mới" từ giá trị đang nhập, ghi đè hoặc xóa preset của người dùng. Nút "Mặc định" dùng preset Hộp tiêu chuẩn.
- **Khi nạp file**: kiểm tra `schemaVersion`; bỏ qua khóa lạ; thông số thiếu thì lấy giá trị mặc định; giá trị ngoài min/max hoặc vi phạm ràng buộc thì đánh dấu preset lỗi và không cho chọn. File hỏng hoặc không đọc được thì báo và dùng bộ preset mẫu, không làm add-in lỗi.
- Đọc và ghi bằng JSON, không dùng `BinaryFormatter`. Ghi file qua file tạm rồi đổi tên để tránh hỏng file nếu bị ngắt giữa chừng.
- "Lưu tạm" ở đây nghĩa là cách lưu đơn giản cho bản đầu. Nếu sau này cần chia sẻ cho cả nhóm thì có thể đặt file ở thư mục dùng chung mà không đổi cấu trúc.

### Có thể làm sau
- Tự động áp dụng sau khi người dùng ngừng nhập một khoảng ngắn (tắt mặc định vì mỗi lần là một lần tính lại model).
- Xem trước 3D hoặc ảnh isometric trong canvas, hoặc vẽ xem trước ngay trong cửa sổ đồ họa của Inventor.

---

## 6. Kiểm thử (thủ công)

| Tình huống | Kỳ vọng |
|------------|---------|
| Tạo đối tượng với giá trị mặc định | Model đúng kích thước |
| Đổi từng thông số về min và max | Model cập nhật, không lỗi |
| Nhập giá trị ngoài khoảng cho phép | UI chặn, model không đổi |
| Nhập giá trị làm hình học bất khả thi | Hoàn tác, thông báo rõ |
| Ctrl+Z sau khi chỉnh | Quay về đúng trạng thái trước |
| Lưu, đóng, mở lại rồi chỉnh tiếp | Add-in vẫn nhận ra đối tượng |
| Mở file Inventor bình thường không do add-in tạo | Add-in không báo lỗi, chỉ vô hiệu hóa chức năng chỉnh |
| Bấm tạo khi không có document nào mở | Thông báo rõ, không lỗi |
| Bấm tạo khi đang mở Assembly hoặc Drawing | Thông báo chỉ hỗ trợ Part |
| Bấm tạo lần hai trên Part đã có hộp | Không tạo trùng, chuyển sang chỉnh thông số |
| Bấm tạo trên Part đã có hình học hoặc Parameter `BX_` của người khác | Cảnh báo, không ghi đè |
| Tạo thất bại giữa chừng | Part trở về nguyên trạng ban đầu |
| Gõ hoặc kéo slider từng thông số | Canvas đổi ngay, model Inventor chưa đổi cho tới khi bấm Áp dụng |
| Focus vào một ô nhập | Kích thước tương ứng được tô nổi bật trên canvas |
| Đặt giá trị ở min và max của từng thông số | Canvas vẫn hiển thị rõ, không vỡ hình |
| Nhập giá trị vi phạm ràng buộc (vd Wall quá lớn) | Ô đỏ, nút Áp dụng bị khóa, canvas không vẽ hình sai |
| Kéo giãn hoặc thu nhỏ cửa sổ dock | Canvas co giãn đúng tỷ lệ, không méo |
| Đổi theme sáng/tối của Inventor | UI và canvas vẫn đọc được |
| Đóng Inventor khi add-in đang mở | Thoát sạch, không treo tiến trình |

---

## 7. Rủi ro và cách giảm

| Rủi ro | Cách giảm |
|--------|-----------|
| Sketch bị mất ràng buộc hoặc đảo chiều khi đổi thông số | Ràng buộc đầy đủ khi tạo, test ở giá trị biên |
| Sai đơn vị (cm so với mm) | Luôn dùng expression có đơn vị, tập trung việc chuyển đổi ở một chỗ |
| Add-in không nạp được trên máy khác | Chốt phiên bản Inventor, kiểm tra file `.addin` và đường dẫn |
| Rò rỉ COM làm Inventor không thoát | Quy tắc giải phóng tham chiếu, kiểm thử đóng app |
| Người dùng tự sửa Parameter làm lệch trạng thái | Đọc lại giá trị từ model mỗi lần mở UI, không cache lâu |
| Host WPF vào dockable window của Inventor gặp lỗi hiển thị hoặc focus | Thử UserControl rỗng ngay ở Giai đoạn 1, trước khi làm form và canvas |
| Canvas khác với hình thật của Inventor khiến người dùng hiểu sai | Ghi chú rõ đây là sơ đồ minh họa, vẽ đúng tỷ lệ, chỉ làm xem trước 3D sau khi bản 2D ổn |
| Gõ từng phím làm tính lại model nhiều lần, UI giật | Canvas dùng lớp tính hình thuần, chỉ đẩy vào Inventor khi bấm Áp dụng |

---

## 8. Việc làm ngay tiếp theo

1. Chốt 6 câu hỏi ở mục 2.
2. Điền bảng thông số ở mục 4 cho hình dạng mẫu đầu tiên.
3. Làm xong Giai đoạn 0 và 1 để có khung chạy được, sau đó mới sang tạo đối tượng.
