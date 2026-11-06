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
| 3 | Hình dạng mục tiêu | **Máy biến áp lực tham chiếu ảnh GE Vernova (đã chốt hướng), làm theo từng cấp** | **Cấp 1 chính là hộp rỗng có nắp** (thùng và nắp), thông số ở mục 4. Các cấp sau thêm sứ, ống dẫn, làm mát. Chi tiết ở mục 5d |
| 4 | Danh sách thông số | Tên, kiểu, đơn vị, min/max, mặc định | Lập bảng ở mục 4 |
| 5 | Tạo và chỉnh ở đâu | **Trên model (Part) đang mở (đã chốt)** | Không tạo document mới. Add-in làm việc với document đang active. Quy tắc xử lý xem Giai đoạn 2 |
| 6 | UI | **UI chính chuyển sang web (xem mục 15 của `cadflow-order-to-model-plan.md`).** Dockable window WPF kèm canvas xem trước nay là công cụ phát triển, tùy chọn | Canvas xem trước trở thành xem trước trên web. Mục 5b giữ lại làm tham khảo thiết kế |
| 7 | Lưu preset | **File JSON (đã chốt, lưu tạm)** | File mẫu `presets.json` đi kèm kế hoạch. Chi tiết ở mục 5b |
| 8 | Cách mô tả đối tượng | **Tự xây ngôn ngữ riêng (DSL) cho Inventor (đã chốt hướng)** | Lấy cảm hứng từ ý tưởng rule-based của SmartAssembly nhưng cú pháp và ngữ nghĩa do mình tự thiết kế. Chi tiết ở mục 5c |

---

## 3. Kiến trúc đề xuất

### 3.1. Phân lớp
- **AddIn Host**: lớp server của add-in, quản lý vòng đời, đăng ký ribbon, dọn tài nguyên khi tắt.
- **UI Layer (WPF, theo MVVM)**: View chỉ binding, ViewModel giữ trạng thái và lệnh, không chứa logic Inventor; ViewModel chỉ gọi **giao diện dịch vụ**, không tham chiếu Inventor trực tiếp. Chi tiết ở mục 16.3 của `cadflow-order-to-model-plan.md`. Backend là ASP.NET Core, frontend web là ReactJS.
- **Application Layer**: các use case như `TạoĐốiTượng`, `CậpNhậtThôngSố`, `ĐọcThôngSố`.
- **Inventor Adapter**: lớp duy nhất gọi trực tiếp Inventor API. Các lớp khác chỉ làm việc với model của riêng mình.
- **Domain/Model**: định nghĩa đối tượng và thông số (tên, đơn vị, ràng buộc), độc lập với Inventor để dễ test.
- **Lớp ngôn ngữ (DSL)**: lexer, parser, phân tích ngữ nghĩa, bộ tính biểu thức có đơn vị, sinh "kế hoạch dựng" trừu tượng. Hoàn toàn độc lập với Inventor, chi tiết ở mục 5c.

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
- Thêm loại đối tượng thứ hai để kiểm chứng kiến trúc, đồng thời là bước D4 của DSL (mục 5c).
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

## 5c. Ngôn ngữ riêng (DSL) để mô tả đối tượng Inventor

### Ý tưởng
SmartAssembly (cho Creo) cho người dùng viết chương trình bằng ngôn ngữ riêng của nó để tự động hóa thiết kế dựa trên luật. Add-in này lấy **ý tưởng** đó: đối tượng, thông số, luật và các bước dựng được mô tả bằng một ngôn ngữ do mình tự thiết kế, add-in chỉ là trình thông dịch và thực thi trong Inventor. Cú pháp và ngữ nghĩa không sao chép từ SmartAssembly.

### Mục tiêu
1. Một **file mô tả** là nguồn duy nhất cho một loại đối tượng: thông số, ràng buộc, giá trị suy ra, các bước dựng.
2. **UI và canvas sinh từ file mô tả** (danh sách ô nhập, min/max, thông báo lỗi), không viết tay từng loại đối tượng.
3. Thêm loại đối tượng mới chủ yếu là viết file mô tả mới, ít hoặc không sửa lõi add-in.
4. Thông báo lỗi chỉ rõ **dòng và cột** trong file mô tả.

### Ngoài phạm vi ban đầu
- Lắp ráp từ trên xuống kiểu SmartAssembly (đặt nhiều thành phần vào Assembly).
- Trình soạn thảo riêng có tô màu cú pháp, gợi ý code.
- Chạy ngôn ngữ ngoài Inventor hoặc trên web.

### Đường ống xử lý
1. **Lexer**: chia văn bản thành token, ghi vị trí dòng/cột.
2. **Parser**: tạo cây cú pháp (AST), báo lỗi cú pháp có vị trí và gợi ý sửa.
3. **Phân tích ngữ nghĩa**: kiểm tra tên chưa khai báo hoặc khai báo trùng, kiểm tra kiểu và **đơn vị** (không cộng mm với độ), phát hiện phụ thuộc vòng giữa các giá trị suy ra, sắp thứ tự tính.
4. **Bộ tính (evaluator)**: tính biểu thức có đơn vị, kiểm tra các luật, trả về danh sách vi phạm kèm thông báo.
5. **Kế hoạch dựng (build plan)**: danh sách thao tác trừu tượng theo thứ tự (tạo sketch, extrude, shell, bo góc, đặt tên body...), chưa gọi Inventor.
6. **Inventor Adapter**: duyệt kế hoạch dựng và gọi Inventor API, trong một Transaction.
7. **Preview renderer** của canvas đọc cùng kết quả đã tính, không qua Inventor.

Mỗi bước đều test được mà **không cần mở Inventor**, chỉ riêng bước 6 cần.

### Các khái niệm ngôn ngữ cần có
| Khái niệm | Vai trò | Ghi chú |
|-----------|---------|---------|
| Khai báo đối tượng | Tên, mô tả, tiền tố Parameter | Mỗi file là một loại đối tượng |
| Thông số (parameter) | Tên, kiểu (độ dài, góc, số nguyên, văn bản), đơn vị, mặc định, min/max | Chính là bảng ở mục 4 |
| Giá trị suy ra (derived) | Tính từ thông số khác | Ví dụ bán kính bo góc trong |
| Luật/ràng buộc (rule) | Điều kiện phải đúng, kèm thông báo lỗi | Chính là danh sách "Ràng buộc hợp lệ" ở mục 4 |
| Biểu thức | Số học, so sánh, hàm `min`/`max`, đơn vị | Là lõi của ngôn ngữ |
| Bước dựng (build step) | Sketch, feature, tên body | Theo thứ tự, tham chiếu thông số bằng tên |
| Điều kiện, lặp (sau) | Bật tắt một phần, số lượng phần tử theo thông số số nguyên | Cần cho gân, mảng lỗ, trục bậc |
| Import/thư viện (sau) | Dùng lại định nghĩa | Cho thư viện thành phần |
| Preset | Bộ giá trị đặt tên | Giữ nguyên file JSON, không nhất thiết đưa vào DSL |

### Các quyết định thiết kế ngôn ngữ (bạn tự quyết)
| # | Câu hỏi | Lựa chọn | Gợi ý tham khảo |
|---|---------|----------|-----------------|
| D1 | Phong cách | **Đã chọn hướng: mệnh lệnh theo dòng, từ khóa viết hoa, ba khối (form, bảng, dựng), kiểu SmartAssembly** | Chi tiết ánh xạ ở `smartassembly-to-inventor-mapping.md`. Thay cho phương án lai ở bản trước; bước "kế hoạch dựng" nay là giao diện Host và vết lệnh (mục 14 của file đó) |
| D2 | Cú pháp bên ngoài | Văn bản tự thiết kế / dựa trên JSON hoặc YAML | Nếu muốn ngôn ngữ riêng thật sự thì dùng văn bản tự thiết kế; JSON chỉ là phương án tạm |
| D3 | Cách viết parser | Viết tay (recursive descent) / dùng thư viện (ví dụ Pidgin, Superpower, Sprache) / ANTLR | Viết tay cho ngôn ngữ nhỏ cho thông báo lỗi tốt nhất; thư viện nhanh hơn lúc đầu |
| D4 | Kiểu và đơn vị | Có kiểm tra đơn vị tĩnh / chỉ kiểm tra lúc chạy | Nên kiểm tra tĩnh, vì sai đơn vị là lỗi phổ biến nhất |
| D5 | Tên tham chiếu đến Inventor | Tên thông số map thẳng sang tên Parameter / có ánh xạ | Map thẳng, dùng tiền tố khai báo trong file |
| D6 | Đuôi file và nơi đặt | **Đuôi `.gea` (đã chốt)**; thư mục thư viện theo sản phẩm | Đặc tả ở `gea-language-spec.md`, file mẫu `tank-level1.gea` |
| D7 | Phiên bản ngôn ngữ | Có `version` trong file không | Nên có từ đầu để sau này đổi cú pháp được |
| D8 | Thông báo lỗi | Mức chi tiết, ngôn ngữ (Việt/Anh) | Có dòng, cột, đoạn trích, gợi ý |

### Các bước phát triển DSL
| Bước | Nội dung | Hoàn thành khi |
|------|----------|----------------|
| D0 | Viết **bằng tay** vài file mẫu mô tả hộp theo cú pháp bạn muốn, chưa có parser. Chốt các quyết định D1-D8 qua việc viết thử | Có file mô tả hộp đầy đủ mà bạn đọc vào thấy tự nhiên |
| D1 | Lexer, parser, AST cho phần thông số và luật. Chưa cần Inventor | Đọc được file hộp, báo lỗi cú pháp đúng dòng cột |
| D2 | Phân tích ngữ nghĩa và bộ tính biểu thức có đơn vị | Với bộ giá trị cho trước trả đúng danh sách luật bị vi phạm. UI và canvas đọc được định nghĩa |
| D3 | Phần bước dựng và kế hoạch dựng, Adapter gọi Inventor | Cùng file mô tả dựng ra hộp giống hệt phiên bản viết cứng của Giai đoạn 2 |
| D4 | Loại đối tượng thứ hai (mặt bích hoặc trục bậc) | Chạy được bằng file mới, không sửa lõi. Lúc này mới bổ sung điều kiện, lặp, số nguyên |
| D5 | Thư viện, import, thông báo lỗi tốt hơn | Dùng lại được phần định nghĩa chung |

### Quan hệ với các giai đoạn khác
- **Giai đoạn 2 vẫn dựng hộp bằng code viết cứng** trước. Việc này giúp bạn nắm Inventor API và tạo ra **bản chuẩn để so sánh**: DSL dựng ra phải cho kết quả giống bản viết cứng (cùng Parameter, cùng body, cùng thể tích).
- D0 và D1, D2 có thể làm song song với Giai đoạn 0-1, vì không cần Inventor.
- `presets.json` giữ nguyên; khóa là tên thông số đã khai báo trong file mô tả.

### Kiểm thử cho DSL
- **Unit test lexer, parser**: file đúng và nhiều dạng file sai, kiểm tra vị trí lỗi.
- **Test ngữ nghĩa**: đơn vị không khớp, tên chưa khai báo, phụ thuộc vòng.
- **Test luật**: bộ giá trị biên và vi phạm cho trước, kiểm tra danh sách lỗi.
- **Golden test**: file mô tả và bộ giá trị cho trước, so sánh kế hoạch dựng với kết quả mong đợi đã lưu.
- **So sánh với bản viết cứng** trong Inventor: DSL và code viết cứng cho cùng model.

---

## 5d. Đối tượng mục tiêu: máy biến áp lực (tham chiếu ảnh GE Vernova)

### Về đối tượng
- Ảnh tham chiếu là **một máy biến áp lực cỡ lớn** (một thiết bị trong trạm biến áp), không phải cả trạm. Add-in sẽ dựng **mô hình đơn giản hóa, tham số hóa** có hình dáng tổng thể tương tự, không phải bản sao của sản phẩm cụ thể nào.
- Ảnh chỉ dùng để tham khảo hình khối chung. Không sao chép chi tiết thiết kế riêng, nhãn hiệu hoặc logo.
- Kích thước thật (theo công suất, cấp điện áp, tiêu chuẩn) **do bạn cung cấp từ catalog hoặc bản vẽ**. Kế hoạch này chỉ định nghĩa cấu trúc và tên thông số, không tự đặt số liệu kỹ thuật.

### Các khối nhìn thấy trong ảnh
| Khối | Mô tả (theo ảnh, có thể cần chỉnh) |
|------|--------------------------------------|
| Thùng (tank) | Khối chữ nhật lớn, thành có các tấm gia cường và nắp kiểm tra tròn (manhole); chân đế phía dưới có các khe |
| Nắp và hộp dẫn trên nắp | Phần nắp nâng lên dạng hộp nghiêng chạy dọc thùng |
| Các hộp dẫn (bus duct/turret) | Các khối chữ nhật nghiêng nối từ nắp ra từng sứ xuyên |
| Sứ xuyên (bushing) | Nhiều sứ dài đặt **gần nằm ngang**, một sứ rất dài đặt nghiêng, vài sứ nhỏ dựng đứng; thân sứ có các tán (sheds) |
| Ống làm mát | Ống đường kính lớn có mặt bích, đi vòng và uốn theo đường 3D, có van màu đỏ |
| Bộ làm mát tách rời | Một cụm thiết bị riêng đặt ngoài thùng, nối bằng ống |
| Phụ kiện | Van, nắp chụp màu đỏ ở chân, thang, vòng nâng |

### Chia thành các cấp (độ chi tiết tăng dần)
| Cấp | Nội dung | Cách chứa trong file | Ghi chú |
|-----|----------|----------------------|---------|
| **1. Thùng và nắp** | Chính là hộp rỗng có nắp ở mục 4, đổi tên thành thùng và nắp; thêm chân đế | Part, nhiều body | Làm trước, kiểm chứng toàn bộ vòng lặp tạo và chỉnh |
| **2. Nắp nâng, hộp dẫn, gia cường** | Phần nắp nâng nghiêng, các hộp dẫn nghiêng, tấm gia cường, nắp kiểm tra tròn | Part, nhiều body | Thêm thông số số nguyên (số gia cường) và góc nghiêng |
| **3. Sứ xuyên** | Họ sứ xuyên tham số hóa (đường kính, chiều dài, số tán), đặt theo vị trí và góc | Part nhiều body hoặc **Assembly** | Số lượng sứ thay đổi theo cấu hình; nên làm sứ là chi tiết dùng lại |
| **4. Ống làm mát và van** | Ống đi theo đường 3D có điểm uốn, mặt bích, van | **Assembly** | Phần khó nhất của hình học (quét theo đường 3D) |
| **5. Bộ làm mát tách rời và phụ kiện** | Cụm làm mát ngoài, thang, vòng nâng, bánh hoặc đế trượt | **Assembly** | Dùng thư viện thành phần có phiên bản |

### Thay đổi so với quyết định "Part trước"
- Cấp 1 và 2 vẫn là **một Part nhiều body**, giữ đúng kế hoạch hiện tại.
- Từ Cấp 3 trở đi nên chuyển sang **Assembly** với thư viện thành phần (sứ, ống, van...). Việc này đưa nội dung của Giai đoạn 5 lên sớm hơn, nhưng chỉ khi Cấp 1-2 đã chạy ổn.
- Add-in, DSL và Adapter cần được thiết kế để dựng được **cả Part lẫn Assembly** ngay từ đầu (chưa cần cài đặt, chỉ cần không khóa cứng giả định "một Part").

### Thông số theo cấp (tên gợi ý, điền giá trị từ catalog)
Tiền tố `TR_` thay cho `BX_` khi chuyển sang máy biến áp. Cấp 1 ánh xạ trực tiếp từ bảng ở mục 4.

| Cấp | Tên Parameter | Ý nghĩa | Kiểu | Đơn vị | Tương ứng cũ |
|-----|---------------|---------|------|--------|---------------|
| 1 | TR_TankLength, TR_TankWidth, TR_TankHeight | Kích thước ngoài thùng | Length | mm | BX_Length, BX_Width, BX_Height |
| 1 | TR_Wall, TR_Bottom, TR_CornerRadius | Độ dày thành, đáy, bo góc | Length | mm | BX_Wall, BX_Bottom, BX_CornerRadius |
| 1 | TR_CoverThickness, TR_CoverLip, TR_CoverClearance | Nắp và gờ nắp | Length | mm | BX_LidThickness, BX_LidLip, BX_LidClearance |
| 1 | TR_SkirtHeight | Chiều cao chân đế | Length | mm | mới |
| 2 | TR_CoverRiseHeight, TR_CoverRiseAngle | Chiều cao và góc nghiêng phần nắp nâng | Length, Angle | mm, độ | mới |
| 2 | TR_RibCount, TR_RibSpacing, TR_RibDepth | Số, bước và độ sâu gân gia cường | Integer, Length | -, mm | mới |
| 2 | TR_ManholeCount, TR_ManholeDiameter | Số và đường kính nắp kiểm tra | Integer, Length | -, mm | mới |
| 3 | TR_BushingCount | Số sứ xuyên | Integer | - | mới |
| 3 | TR_BushingDiameter, TR_BushingLength, TR_BushingShedCount | Kích thước sứ | Length, Integer | mm | mới |
| 3 | TR_BushingAngle, TR_BushingSpacing | Góc đặt và khoảng cách giữa sứ | Angle, Length | độ, mm | mới |
| 4 | TR_PipeDiameter, TR_PipeBendRadius | Ống làm mát | Length | mm | mới |
| 4 | TR_PipeRoute | Đường đi ống (danh sách điểm) | Danh sách | mm | mới |
| 5 | TR_CoolerOffset, TR_CoolerSize | Vị trí và kích thước bộ làm mát | Length | mm | mới |

Một số thông số là **danh sách** (đường đi ống, vị trí từng sứ). Điều này ảnh hưởng đến UI, DSL và preset: ô nhập đơn không đủ, cần bảng hoặc danh sách điểm. Nên xử lý khi đến Cấp 3 và 4, chưa cần ở Cấp 1.

### Ảnh hưởng tới các phần khác của kế hoạch
- **Canvas xem trước**: thêm khung nhìn từ bên cạnh với thùng, nắp nâng và các sứ nghiêng (Cấp 2-3). Ống làm mát (Cấp 4) chỉ cần sơ đồ đường đi.
- **DSL**: cần danh sách, lặp theo số nguyên (số sứ, số gân) và điều kiện sớm hơn dự kiến, vì sứ và gân phụ thuộc số lượng. Nên đưa các tính năng này vào bước D4 của mục 5c.
- **Kiểm thử**: thêm ca số sứ nhỏ nhất và lớn nhất, góc sứ ở hai đầu giới hạn, đường ống có điểm gấp khúc, nhiều sứ chồng lên nhau.
- **Hình ảnh**: mô hình chỉ cần giống về bố cục và tỉ lệ, không cần giống ảnh chụp. Bỏ qua chi tiết nhỏ (bu lông, đường hàn) ở bản đầu.

### Rủi ro riêng của đối tượng này
| Rủi ro | Cách giảm |
|--------|-----------|
| Quá nhiều chi tiết, dự án không bao giờ xong | Làm theo từng cấp, mỗi cấp chạy ổn mới sang cấp sau; chốt "đủ giống" cho từng cấp |
| Ống theo đường 3D dễ lỗi khi đổi kích thước | Dùng đường đi tính sẵn từ thông số, kiểm tra điểm gấp khúc và bán kính uốn tối thiểu |
| Sứ nghiêng làm hộp dẫn và sứ lệch nhau | Tính vị trí hộp dẫn từ vị trí và góc của sứ, không đặt tay |
| Thiếu số liệu thật | Bạn cung cấp bảng kích thước từ catalog; đặt chỗ trống trong bảng thông số cho tới khi có |
| Part một file quá nặng khi có nhiều sứ | Chuyển sang Assembly từ Cấp 3, dùng chi tiết sứ lặp lại |

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
| DSL phình to thành dự án riêng, add-in không bao giờ chạy được | Dựng hộp bằng code viết cứng trước (Giai đoạn 2); chỉ thêm tính năng ngôn ngữ khi một ví dụ thật cần đến |
| Thiết kế cú pháp quá sớm rồi phải đổi, hỏng các file đã viết | D0 viết tay file mẫu trước khi code parser; có `version` trong file |
| Thông báo lỗi khó hiểu khiến người viết file mô tả bó tay | Lexer ghi dòng cột từ đầu, test riêng các trường hợp lỗi |
| Sai đơn vị trong biểu thức | Kiểm tra đơn vị tĩnh ở bước phân tích ngữ nghĩa |
| DSL dựng ra khác bản viết cứng nhưng không ai phát hiện | So sánh Parameter, body và thể tích giữa hai bản trong Inventor |

---

## 8. Việc làm ngay tiếp theo

Các quyết định ở mục 2 và bảng thông số ở mục 4 đã xong. Hai nhánh có thể làm song song:

**Nhánh Inventor**
1. Giai đoạn 0: cài Visual Studio 2026, Inventor 2027 SDK, tạo solution.
2. Giai đoạn 1: khung add-in, ribbon, dockable window chứa UserControl WPF rỗng.
3. Giai đoạn 2: dựng hộp bằng code viết cứng trên Part đang mở (làm bản chuẩn).

**Nhánh ngôn ngữ (không cần Inventor)**
1. D0: viết tay file mô tả hộp theo cú pháp bạn muốn, chốt các quyết định D1-D8 ở mục 5c.
2. D1, D2: lexer, parser, phân tích ngữ nghĩa, bộ tính biểu thức có đơn vị.
3. D3: kế hoạch dựng và Adapter, so sánh với bản viết cứng.
