# Kế hoạch: Hệ thống "đặt hàng → tự động dựng model Inventor → trả hồ sơ"

Tài liệu này nối tiếp `inventor-addin-plan.md`. Add-in và DSL trong kế hoạch đó là **lõi dựng model**; tài liệu này thêm phần **web đặt hàng, hàng đợi và máy worker** để biến lõi đó thành một dịch vụ.

---

## 1. Những gì thấy trong video

Video dài khoảng 43 giây, quay màn hình, **không có tiếng**. Hình mờ nên một số chữ nhỏ là suy đoán.

### 1.1. Giao diện web "CADFlow – Assembly orders"
- Có danh mục sản phẩm (nút "All products"). Sản phẩm demo là **Power transformer** (máy biến áp), tiêu đề ghi "full Smart Assembly demo".
- **Form đặt hàng** gồm:
  - Rating (công suất, ví dụ 1000 kVA) và HV voltage class (cấp điện áp cao áp, ví dụ 35 kV). Có ghi chú nhỏ cho biết **kích thước và giá lấy từ catalog**, và **chỉ cho phép các cấp điện áp hợp lệ với công suất đã chọn**.
  - Cooling (làm mát, hộp chọn có hai giá trị, trông như ONAN và ONAF).
  - Options: air neutral bushing, manhole on the cover, shipping configuration (no breather).
  - Paint (màu sơn, ví dụ Grey, Green).
  - Documents: Drawing and PDF.
  - Requested by, nút **Submit order**.
- **Trang chi tiết job** (mã dạng `AJ-20261105-090449-933f`):
  - Trạng thái chuyển: **chờ máy nhận** ("Waiting for an Inventor worker to pick up the job...") → **đang dựng** ("The worker is building the assembly...") → **thành công**.
  - Hiện Product, Created, Started, Finished, **Worker** (tên máy chạy Inventor), danh sách **Inputs**, và dòng **Summary** (khối lượng kg và giá).
  - Sau khi xong có **Picture** (ảnh chụp model) và mục **Downloads**: PDF drawing, STEP model, BOM, Costing, ZIP package, Report, Picture, Inventor drawing.
- Địa chỉ dạng `100.x.x.x:18080/assembly/jobs/...` và có đường dẫn `api/assembly/...`, nên có một API phía sau. Dải `100.x` thường là mạng riêng kiểu Tailscale (suy đoán).

### 1.2. Máy worker (nửa trái màn hình)
- Đây là một máy Windows (thấy chữ "Activate Windows") mở Inventor, được xem từ xa qua trình duyệt.
- Khi job chạy, **model thay đổi trực tiếp**: thùng máy đổi vật liệu, cuối cùng đổi sang **màu xanh** đúng với tùy chọn sơn đã chọn.
- Sau đó Inventor mở **bản vẽ** tự động: khung nhìn, bóng đánh số (balloon), mặt cắt A-A tỉ lệ 1/50 và **bảng Parts List** (Tank, Cover, hex bolt ISO 4017 M16x70, HV bushing, LV bushing, radiator ONAF, conservator...).
- Model gồm nhiều thành phần: thùng, nắp, sứ xuyên cao áp và hạ áp, cánh tản nhiệt, bình dầu phụ.

### 1.3. Điều video cho thấy và chưa cho thấy
| Cho thấy | Chưa cho thấy |
|----------|---------------|
| Luồng đặt hàng → hàng đợi → worker → hồ sơ trả về | Có dùng AI hay agent hay không (không thấy khung chat hay prompt) |
| Form sinh theo sản phẩm, có ràng buộc giữa các lựa chọn | Cách viết luật bên trong (DSL, SmartAssembly hay code) |
| Dựng assembly, bản vẽ, BOM, giá, STEP, ảnh, ZIP, báo cáo | Cách xử lý lỗi, thử lại, nhiều worker cùng lúc |

Kết luận: đây là hệ thống **cấu hình theo đơn đặt hàng (configure-to-order)** chạy tất định. AI nếu có chỉ cần nằm ở phía trước để biến yêu cầu bằng lời thành các giá trị của form (xem mục 9).

---

## 2. Mục tiêu

1. Người dùng chọn sản phẩm, điền form, bấm gửi.
2. Hệ thống dựng model Inventor đúng theo luật, không cần ai ngồi thao tác.
3. Trả về **bộ hồ sơ**: ảnh, STEP, bản vẽ PDF và IDW, BOM, giá, báo cáo, gói ZIP.
4. Xem được trạng thái job, lịch sử đơn, tải lại hồ sơ.
5. Thêm sản phẩm mới chủ yếu bằng **định nghĩa sản phẩm** (DSL, catalog, mẫu bản vẽ), không sửa lõi.

### Ngoài phạm vi ban đầu
- Thanh toán, báo giá có phê duyệt, phân quyền phức tạp.
- Chạy nhiều Inventor song song trên một máy.
- Xem trước 3D ngay trên web.

---

## 3. Kiến trúc tổng thể

```
Người dùng ──► Web (form, danh sách job, tải file)
                 │
                 ▼
            API Server ──► Cơ sở dữ liệu (sản phẩm, đơn, job, worker)
                 │    └──► Kho file (hồ sơ kết quả)
                 ▼
            Hàng đợi job
                 ▲   (worker chủ động hỏi, chỉ cần kết nối đi ra)
                 │
        Worker (máy Windows có Inventor 2027)
            ├─ Vòng lặp nhận job, báo trạng thái
            ├─ Lõi DSL: đọc định nghĩa sản phẩm, kiểm tra luật, tạo kế hoạch dựng
            ├─ Inventor Adapter: dựng assembly, bản vẽ
            └─ Bộ xuất: STEP, PDF, IDW, ảnh, BOM, giá, báo cáo, ZIP
```

### 3.1. Các thành phần
| Thành phần | Vai trò | Gợi ý |
|------------|---------|-------|
| **Web** | Form sinh theo định nghĩa sản phẩm, trang job, tải file | Dựng form từ schema của sản phẩm, không viết tay từng form |
| **API Server** | Danh mục, nhận đơn, xác thực lại giá trị bằng lõi DSL, quản lý job, cấp file | ASP.NET Core, để dùng chung thư viện DSL với worker |
| **CSDL** | Sản phẩm và phiên bản, đơn, job, artifact, worker | PostgreSQL |
| **Hàng đợi** | Giữ job chờ | Bắt đầu bằng bảng job trong CSDL, worker nhận bằng khóa dòng (`FOR UPDATE SKIP LOCKED`); chưa cần SQS hay broker |
| **Kho file** | Lưu hồ sơ | Thư mục trên server lúc đầu, về sau S3 hoặc R2 |
| **Worker** | Chạy Inventor và dựng hồ sơ | Ứng dụng .NET chạy trên máy Windows có Inventor |
| **Thư viện thành phần** | Part, iPart, assembly mẫu (sứ, cánh tản nhiệt, bình dầu...) | Quản lý phiên bản, đặt trong thư mục cố định hoặc kho chung |

### 3.2. Nguyên tắc
- **Worker hỏi server, server không gọi worker.** Máy chạy Inventor chỉ cần kết nối đi ra, không mở cổng vào, dễ đặt sau tường lửa hoặc trong mạng riêng.
- **Một định nghĩa sản phẩm duy nhất** dùng cho cả ba nơi: form web, kiểm tra luật phía server, dựng model ở worker. Không có luật nào bị viết hai lần.
- **Lõi DSL không phụ thuộc Inventor** (đúng như kế hoạch DSL). Server và worker dùng chung thư viện này.
- **Inventor Adapter không phụ thuộc add-in host**: nhận một đối tượng `Inventor.Application` rồi làm việc. Nhờ vậy cùng một Adapter chạy được trong add-in (dockable window) lẫn trong worker.
- **Mỗi job bất biến và lặp lại được**: lưu cả giá trị đầu vào, phiên bản sản phẩm, phiên bản thư viện thành phần.

---

## 4. Định nghĩa sản phẩm

Một sản phẩm là một **gói** gồm nhiều phần, đều là dữ liệu:

| Phần | Nội dung | Ví dụ (máy biến áp) |
|------|----------|---------------------|
| Thông số và tùy chọn | Tên, kiểu, đơn vị, mặc định, danh sách lựa chọn | Rating, cấp điện áp, làm mát, sơn, các hộp chọn |
| Quan hệ giữa lựa chọn | Lựa chọn nào hợp lệ với lựa chọn nào | Công suất chỉ cho phép một số cấp điện áp |
| Bảng catalog | Kích thước, mã chi tiết, giá theo công suất, vật liệu | Kích thước thùng theo công suất, mã sứ xuyên, giá |
| Luật | Điều kiện và tính toán | Số cánh tản nhiệt theo làm mát, chiều cao bình dầu |
| Kế hoạch dựng | Chi tiết nào đặt ở đâu, thông số nào gán | Đặt thùng, nắp, sứ, bình dầu; bật hoặc tắt lỗ người chui |
| Mẫu bản vẽ | Mẫu IDW, danh sách khung nhìn, bảng, tỉ lệ | Trang A3, mặt cắt A-A, Parts List |
| Công thức giá và báo cáo | Cách tính giá, mẫu báo cáo | Giá = chi tiết + gia công + phụ kiện |

Gợi ý tổ chức:
- Phần luật và kế hoạch dựng chính là **DSL** ở mục 5c của `inventor-addin-plan.md`.
- **Catalog tách khỏi luật**, lưu dạng bảng (CSV hoặc JSON) để người không viết code cập nhật được.
- Mỗi sản phẩm có **phiên bản**. Đơn đã đặt luôn gắn với phiên bản của lúc đặt.

---

## 5. Vòng đời một job

| Trạng thái | Ý nghĩa | Hiển thị trên web |
|------------|---------|-------------------|
| Queued | Đã nhận đơn, chờ worker | Chờ máy Inventor nhận |
| Running | Worker đang làm, có các bước nhỏ | Đang dựng assembly (kèm bước: kiểm tra, dựng, bản vẽ, xuất file, tải lên) |
| Succeeded | Xong, có hồ sơ | Ảnh, tóm tắt (khối lượng, giá), nút tải |
| Failed | Có lỗi | Thông báo lỗi dễ hiểu và nút gửi lại |
| Cancelled | Người dùng hủy | |

### 5.1. Các bước của worker
1. **Nhận job** (khóa job cho worker này, ghi giờ bắt đầu).
2. **Kiểm tra lại đầu vào** bằng lõi DSL. Sai thì dừng, báo lỗi mà chưa mở Inventor.
3. **Chuẩn bị**: mở hoặc kiểm tra Inventor, tạo thư mục làm việc riêng cho job, sao chép thư viện thành phần cần dùng.
4. **Dựng assembly** theo kế hoạch dựng.
5. **Tính chỉ số**: khối lượng từ Inventor, giá từ catalog và công thức.
6. **Tạo bản vẽ** từ mẫu, đặt khung nhìn, balloon, bảng.
7. **Xuất**: ảnh, STEP, PDF, IDW, BOM, bảng giá, báo cáo, gói ZIP.
8. **Tải lên** server, ghi tóm tắt, đặt trạng thái Succeeded.
9. **Dọn dẹp**: đóng tài liệu, xóa thư mục tạm, đưa Inventor về trạng thái sạch.

### 5.2. Độ bền
- **Nhịp tim (heartbeat)**: worker báo định kỳ. Job quá hạn mà không có nhịp thì trả về Queued hoặc đánh dấu lỗi.
- **Thời gian tối đa** cho mỗi bước và cho cả job.
- **Thử lại** có giới hạn số lần. Lỗi do đầu vào sai thì không thử lại.
- **Một job tại một thời điểm cho mỗi Inventor.** Muốn nhanh hơn thì thêm máy worker, không chạy song song trong một Inventor.
- **Bộ nhớ đệm**: cùng sản phẩm, cùng phiên bản và cùng đầu vào thì dùng lại hồ sơ cũ (băm đầu vào để so khớp).
- **Giám sát Inventor**: nếu treo hoặc hộp thoại chặn, kết thúc tiến trình và khởi động lại, job chuyển sang thử lại.

---

## 6. Phần Inventor ở worker

### 6.1. Cách chạy Inventor
| Cách | Ưu | Nhược |
|------|-----|-------|
| **Worker là tiến trình riêng, điều khiển Inventor qua COM** | Dễ gỡ lỗi, tách khỏi Inventor, tái dùng Adapter | Cần Inventor chạy trong một phiên desktop đăng nhập |
| **Add-in chạy trong Inventor, tự nhận job** | Gần API nhất | Phải chuyển lời gọi về luồng UI của Inventor, khó gỡ lỗi |
| **APS Design Automation** (Inventor chạy trên cloud, không cần máy riêng) | Không phải quản lý máy, mở rộng dễ | Phải đóng gói plugin theo yêu cầu của dịch vụ; **cần kiểm tra có hỗ trợ Inventor 2027 và target .NET nào**; giới hạn về thư viện và bản vẽ |

Khuyến nghị: bắt đầu với **tiến trình worker riêng điều khiển Inventor qua COM** trên một máy Windows. Công ty bạn đã dùng APS nên phương án thứ ba đáng để dành, sau khi biết rõ hỗ trợ Inventor 2027.

### 6.2. Lưu ý vận hành Inventor
- Inventor cần **phiên desktop đăng nhập** (không chạy ổn như dịch vụ Windows nền). Cấu hình tự đăng nhập, tự mở worker khi khởi động máy.
- Chạy ở chế độ **không hộp thoại** (`SilentOperation`) và xử lý hộp thoại bất ngờ (thiếu file, giấy phép, cập nhật).
- Giấy phép: mỗi máy worker cần một giấy phép Inventor.
- Dùng thư mục làm việc riêng cho từng job, đường dẫn cố định, tránh xung đột khi dựng assembly nhiều file.

### 6.3. Dựng assembly
- **Thư viện thành phần** là các part và iPart tham số hóa (thùng, nắp, sứ xuyên, cánh tản nhiệt, bình dầu...). Mỗi cái có Parameter được đặt tên theo quy ước như `BX_` ở add-in.
- Kế hoạch dựng nói: chèn chi tiết nào, đặt ở đâu (constraint hoặc vị trí tính sẵn), gán thông số nào, bật hoặc **suppress** chi tiết nào theo tùy chọn (lỗ người chui, sứ trung tính...).
- **Sơn** là gán Appearance cho chi tiết, đúng như video (thùng đổi sang xanh).
- Nên **đặt vị trí bằng tọa độ tính sẵn** hoặc constraint đơn giản và cố định. Constraint phức tạp là nguồn lỗi nhiều nhất khi tự động hóa.

### 6.4. Bản vẽ tự động
- Dùng **mẫu IDW** có sẵn khung tên, bảng Parts List, kiểu kích thước.
- Khung nhìn được đặt theo bố cục khai báo trong định nghĩa sản phẩm (tỉ lệ, vị trí, mặt cắt).
- Balloon và kích thước tự động có thể sai vị trí hoặc chồng chéo. Đây là phần **khó và dễ vỡ nhất**, nên làm sau cùng và chấp nhận đặt balloon theo luật đơn giản.
- Xuất PDF từ IDW bằng translator của Inventor.

### 6.5. Các hồ sơ xuất ra
| Hồ sơ | Nguồn |
|-------|-------|
| Ảnh (PNG) | Chụp từ khung nhìn Inventor ở góc cố định |
| STEP | Translator của Inventor |
| PDF và IDW bản vẽ | Từ bản vẽ tự động |
| BOM | Đọc từ BOM của assembly hoặc từ kế hoạch dựng, xuất CSV hoặc XLSX |
| Costing | Công thức giá từ catalog và khối lượng |
| Report | Tổng hợp thông số, khối lượng, giá, danh sách file (PDF hoặc HTML) |
| ZIP | Đóng gói các file trên |

---

## 7. Mô hình dữ liệu (mức khái niệm)

| Bảng | Nội dung chính |
|------|----------------|
| Product | Mã, tên, mô tả |
| ProductVersion | Sản phẩm, số phiên bản, gói định nghĩa (DSL, catalog, mẫu bản vẽ), trạng thái xuất bản |
| Order/Job | Mã job, sản phẩm và phiên bản, **đầu vào dạng JSON**, người yêu cầu, trạng thái, bước hiện tại, giờ tạo/bắt đầu/kết thúc, worker, tóm tắt (khối lượng, giá), lỗi, băm đầu vào |
| Artifact | Job, loại (PDF, STEP, BOM...), đường dẫn, kích thước, băm |
| Worker | Tên máy, trạng thái, nhịp tim cuối, phiên bản Inventor và phiên bản thư viện |
| JobEvent | Nhật ký các bước và thông báo của từng job |

Mã job theo dạng có ngày giờ và đoạn ngẫu nhiên (như `AJ-20261105-090449-933f` trong video) để dễ đọc và không trùng.

---

## 8. API (mức tài nguyên, chưa chi tiết)

| Nhóm | Việc cần làm |
|------|--------------|
| Danh mục | Liệt kê sản phẩm, lấy schema form của một sản phẩm |
| Đơn | Gửi đơn (server kiểm tra lại bằng lõi DSL), liệt kê job, xem chi tiết job |
| Hồ sơ | Tải từng file, tải gói ZIP |
| Worker | Nhận job, báo tiến trình và nhịp tim, báo hoàn thành hoặc lỗi, tải file lên |

Cập nhật trạng thái trên web: bắt đầu bằng **polling** định kỳ; nâng lên SSE hoặc SignalR khi cần.

---

## 9. Lớp AI tùy chọn (không bắt buộc)

Nếu muốn có "agent":
- AI chỉ làm một việc: **đọc yêu cầu bằng lời hoặc tài liệu của khách và điền vào form** (hoặc đề xuất giá trị cho người dùng duyệt).
- Giá trị phải đi qua **kiểm tra luật của lõi DSL** như mọi đơn khác. AI không bao giờ tự dựng model hay bỏ qua luật.
- Cách này giữ kết quả tất định, kiểm thử được, và AI chỉ là một cách khác để nhập đơn.
- Làm sau cùng, khi pipeline đã ổn.

---

## 10. Bảo mật và vận hành

- Chạy trong **mạng nội bộ hoặc VPN**. Video dùng HTTP trần trên mạng riêng; khi mở rộng nên có HTTPS và đăng nhập.
- Worker xác thực với server bằng khóa riêng cho từng máy.
- Hồ sơ có thể chứa thông tin giá và thiết kế: phân quyền xem theo người dùng hoặc nhóm.
- Giới hạn kích thước và loại file tải lên.
- Nhật ký đầy đủ cho từng job (phục vụ tìm lỗi).
- Sao lưu CSDL và kho file; thư viện thành phần có kiểm soát phiên bản.

---

## 11. Các giai đoạn thực hiện

### G0. Chọn sản phẩm thí điểm và khung dịch vụ giả lập
- Chọn **hộp rỗng có nắp** (đã có bảng thông số) làm sản phẩm thí điểm, không bắt đầu bằng máy biến áp.
- Dựng web, API, CSDL, hàng đợi, trang job, kho file.
- **Worker giả**: nhận job, chờ vài giây, trả một file mẫu. Kiểm tra toàn bộ luồng và các trạng thái mà chưa cần Inventor.
- **Hoàn thành khi**: đặt đơn trên web, thấy job chạy qua Queued → Running → Succeeded và tải được file.

### G1. Worker thật dựng hộp
- Worker mở Inventor, dùng lõi DSL và Adapter (từ `inventor-addin-plan.md`) dựng hộp.
- Xuất ảnh và STEP.
- Xử lý lỗi, thời gian tối đa, khởi động lại Inventor khi treo.
- **Hoàn thành khi**: đơn hộp trên web cho ra ảnh và STEP đúng với thông số.

### G2. Bản vẽ, BOM, PDF
- Mẫu IDW cho hộp, khung nhìn, bảng, xuất PDF và IDW.
- BOM và báo cáo.
- **Hoàn thành khi**: gói hồ sơ đầy đủ cho hộp.

### G3. Catalog, giá, luật giữa các lựa chọn
- Bảng catalog, công thức giá, khối lượng từ Inventor.
- Form web chỉ cho phép các lựa chọn hợp lệ (như rating và cấp điện áp trong video).
- **Hoàn thành khi**: đổi lựa chọn thì giá và danh sách hợp lệ đổi theo.

### G4. Sản phẩm nhiều thành phần (máy biến áp lực)
- Sản phẩm mục tiêu là máy biến áp lực mô tả ở mục 5d của `inventor-addin-plan.md` (tham chiếu ảnh GE Vernova). Các cấp 1-2 của máy biến áp chính là hộp thí điểm ở G0-G2, nên không phải làm lại.
- Thư viện thành phần có phiên bản, assembly nhiều chi tiết, tùy chọn bật tắt, sơn.
- Thêm sản phẩm thứ hai gần với máy biến áp (ví dụ vỏ tủ có cánh tản nhiệt) để kiểm chứng DSL và Adapter.
- **Hoàn thành khi**: thêm sản phẩm mới chủ yếu bằng định nghĩa.

### G5. Vận hành và mở rộng
- Nhiều worker, bộ nhớ đệm, thông báo, phân quyền, HTTPS.
- Cân nhắc APS Design Automation.
- Lớp AI nhập đơn nếu cần.

---

## 12. Quan hệ với kế hoạch add-in

| Phần trong `inventor-addin-plan.md` | Vai trò trong hệ thống này |
|--------------------------------------|----------------------------|
| DSL (mục 5c) | Ngôn ngữ định nghĩa sản phẩm, dùng ở web, server và worker |
| Inventor Adapter | Dùng nguyên ở worker, tách khỏi add-in host |
| Add-in và dockable window | Công cụ thử nghiệm và chỉnh tay trên máy kỹ sư; vẫn dùng được để dựng và xem trước từng định nghĩa |
| Canvas xem trước, preset | Có thể tái dùng cho form web (xem trước kích thước, preset đơn mẫu) |
| Giai đoạn 2 (dựng hộp viết cứng) | Vẫn giữ làm **bản chuẩn** để so sánh với kết quả DSL |

Thứ tự gợi ý: làm **G0** (không cần Inventor) song song với **Giai đoạn 0-2** của add-in và nhánh DSL, rồi nối chúng lại ở **G1**.

---

## 13. Rủi ro và cách giảm

| Rủi ro | Cách giảm |
|--------|-----------|
| Inventor treo, hộp thoại chặn, giấy phép | Chế độ không hộp thoại, giám sát và khởi động lại, thời gian tối đa, thử lại có giới hạn |
| Bản vẽ tự động sai vị trí, balloon chồng chéo | Làm sau cùng, bố cục cố định, kiểm tra bằng bộ ảnh mẫu, chấp nhận mức đơn giản trước |
| Constraint assembly phức tạp dễ vỡ | Đặt vị trí tính sẵn hoặc constraint đơn giản, thử ở giá trị biên |
| Phạm vi phình quá sớm (máy biến áp ngay từ đầu) | Thí điểm bằng hộp, chỉ thêm tính năng khi sản phẩm thật cần |
| Thư viện thành phần đổi làm hỏng đơn cũ | Phiên bản hóa thư viện, ghi phiên bản vào job |
| Luật nằm rải rác ở web, server và worker | Một định nghĩa duy nhất, dùng chung thư viện DSL |
| Một máy worker là điểm nghẽn | Thiết kế worker kéo job từ ngay đầu để thêm máy sau này dễ |
| APS Design Automation chưa hỗ trợ Inventor 2027 | Kiểm tra trước khi đầu tư; giữ phương án worker riêng làm chính |
| Rò rỉ giá và thiết kế | Mạng riêng, HTTPS, phân quyền theo người dùng |

---

## 14. Việc làm ngay tiếp theo

1. Chốt **sản phẩm thí điểm** (khuyến nghị: hộp rỗng có nắp) và các loại hồ sơ cần trả ở bản đầu (ảnh, STEP, bản vẽ PDF, BOM, giá).
2. Chốt nơi đặt hệ thống (máy chủ web) và **máy Windows làm worker** (có giấy phép Inventor 2027, đăng nhập sẵn).
3. Làm **G0**: web, API, hàng đợi và worker giả. Không phụ thuộc Inventor.
4. Song song, tiếp tục D0 của DSL và Giai đoạn 0-2 của add-in.
5. Chốt các câu hỏi mở:
   - Ai là người dùng (kỹ sư trong công ty hay khách bên ngoài)?
   - Cần mấy loại hồ sơ ở bản đầu?
   - Có sẵn thư viện chi tiết (part, assembly) để dựng sản phẩm thật không?

---

## 15. Giao diện web theo kiểu SmartAssembly

### 15.1. Cách hiểu yêu cầu
Bạn muốn luồng giống SmartAssembly (người dùng nhập yêu cầu, **luật chạy tự động**, CAD chạy phía sau, kết quả trả về) nhưng **mọi tương tác của người dùng diễn ra trên web**, không phải trong cửa sổ của Inventor. Theo thông tin công khai, Sigmaxim cũng có công cụ cấu hình trên web và khái niệm "mô hình chủ ảo" (virtual master model); mình không biết chi tiết bên trong nên chỉ lấy ý tưởng chung: **giao diện cấu hình tách khỏi CAD, luật chạy ở server, CAD là bộ thực thi phía sau**.

Hệ quả cho các quyết định trước:
- **UI chính chuyển từ dockable window WPF sang web.** Canvas xem trước trở thành phần xem trước trên web. Add-in WPF chỉ còn là công cụ phát triển (hoặc bỏ). Điều này cũng loại bỏ rủi ro host WPF vào cửa sổ dock của Inventor ở Giai đoạn 1.
- **Preset JSON** chuyển thành preset lưu trong CSDL theo từng sản phẩm.
- Lõi DSL, Inventor Adapter và worker giữ nguyên vai trò.

### 15.2. Ba khu chức năng trên web

| Khu | Người dùng | Chức năng chính |
|-----|-----------|-----------------|
| **Configurator** (đặt hàng) | Người đặt hàng | Chọn sản phẩm; form sinh từ định nghĩa; kiểm tra luật ngay khi chọn (lựa chọn nào bị khóa, giá trị nào hợp lệ); xem trước; giá sơ bộ; gửi đơn |
| **Orders và Viewer** (theo dõi, kết quả) | Người đặt hàng | Danh sách job, trạng thái cập nhật trực tiếp, xem ảnh và model 3D thật, bản vẽ PDF, tải hồ sơ |
| **Product Studio** (soạn sản phẩm) | Kỹ sư viết định nghĩa | Soạn DSL, bảng catalog, mẫu bản vẽ; báo lỗi theo dòng cột; chạy thử không cần Inventor; xuất bản phiên bản |
| Admin | Quản trị | Worker, job, thư viện thành phần, người dùng |

### 15.3. Luồng người dùng

1. Mở danh mục, chọn sản phẩm (ví dụ máy biến áp lực).
2. Form hiện ra, sinh từ **schema của sản phẩm**: ô nhập, hộp chọn, ô đánh dấu, đơn vị.
3. Khi đổi một lựa chọn, web gọi **lõi DSL** (ở server, hoặc trong trình duyệt nếu chạy WebAssembly) để: khóa hoặc mở các lựa chọn phụ thuộc, báo giá trị vi phạm luật, cập nhật giá sơ bộ và khung xem trước.
4. Bấm Gửi đơn. Server kiểm tra lại lần cuối rồi tạo job.
5. Trang job hiện **Queued → Running (kèm bước) → Succeeded** theo thời gian thực.
6. Khi xong: ảnh từ Inventor, **model 3D xoay được** trong trình duyệt, bản vẽ PDF, BOM, giá, báo cáo, STEP, IDW, ZIP.
7. Người dùng có thể **nhân bản đơn** (lấy đầu vào cũ, sửa vài giá trị, gửi lại) hoặc lưu thành preset.

### 15.4. Xem trước theo ba tầng

| Tầng | Khi nào | Nguồn | Độ chính xác |
|------|---------|-------|--------------|
| 1. Xem trước tức thì | Khi đang chỉnh form | Kế hoạch dựng chạy trong lõi DSL, vẽ ở trình duyệt (sơ đồ 2D như canvas đã thiết kế, sau đó khối 3D thô bằng WebGL, ví dụ three.js) | Minh họa, đúng tỉ lệ nhưng không phải Inventor |
| 2. Ảnh thật | Sau khi worker dựng xong | Ảnh chụp từ Inventor | Chính xác |
| 3. Model 3D thật | Sau khi xong | Chuyển model thành định dạng xem được trên web | Chính xác, xoay và phóng được |

Cách có model 3D thật trên web:

| Cách | Ưu | Nhược |
|------|-----|-------|
| **APS Viewer** (đưa file lên APS, Model Derivative dịch, xem bằng Viewer) | Công ty bạn đã dùng APS (Model Derivative, Viewer), xem được cấu trúc cây và đo | Cần tài khoản APS, tốn thời gian dịch và chi phí, dữ liệu rời khỏi mạng nội bộ |
| **STEP sang glTF tự chuyển** (ví dụ dùng OpenCascade ở worker hoặc server) rồi xem bằng three.js | Chạy hoàn toàn nội bộ, không phụ thuộc cloud | Tự lo chất lượng lưới và màu sắc, phải bảo trì bộ chuyển |
| **Chỉ ảnh nhiều góc** (chụp 4-6 góc từ Inventor) | Rất đơn giản, đủ cho bản đầu | Không xoay tự do |

Khuyến nghị lộ trình: bản đầu chỉ ảnh nhiều góc, rồi thêm APS Viewer (vì đã quen), cân nhắc glTF nội bộ nếu có yêu cầu bảo mật dữ liệu.

### 15.5. Product Studio: soạn sản phẩm trên web
Phần này **tốn công nhất, nên làm sau**.

- Bản đầu: người viết định nghĩa **sửa file trong repo bằng trình soạn thảo thông thường** (kèm công cụ dòng lệnh kiểm tra lỗi và chạy thử), chưa cần web editor.
- Bản sau (web editor):
  - Soạn DSL với tô màu cú pháp tùy biến, báo lỗi dòng cột và gợi ý tên thông số (ví dụ dùng Monaco Editor).
  - **Chạy thử (dry-run)** không cần Inventor: kiểm tra cú pháp, chạy luật với bộ giá trị mẫu, ra kế hoạch dựng, giá, BOM dự kiến và khung xem trước.
  - **Bộ giá trị mẫu** lưu cùng sản phẩm, chạy lại tự động mỗi lần sửa như test hồi quy.
  - **Nháp và xuất bản**: chỉ phiên bản đã xuất bản mới nhận đơn; đơn cũ giữ phiên bản cũ.
  - Nút **chạy thử trên worker** tạo job thử (không tính là đơn thật).
  - So sánh hai phiên bản.
- Chỉnh bảng catalog trên web (dạng bảng) cho người không viết code.

### 15.6. Công nghệ gợi ý

| Phần | Lựa chọn | Ghi chú |
|------|----------|---------|
| Frontend | React hoặc Next.js | Bạn đã quen Next.js; form sinh từ schema JSON |
| Frontend (phương án khác) | Blazor WebAssembly | Chạy cùng thư viện DSL .NET ngay trong trình duyệt, không phải viết lại logic; đổi lại bundle nặng hơn |
| Chạy lõi DSL | Gọi API ở server cho bản đầu | Đơn giản; chuyển sang WebAssembly nếu cần phản hồi tức thì hoặc làm việc ngoại tuyến |
| Editor | Monaco Editor | Cho Product Studio |
| Xem 3D | three.js (xem trước), APS Viewer (model thật) | Xem mục 15.4 |
| API | ASP.NET Core | Dùng chung thư viện DSL với worker |
| Cập nhật trạng thái | Polling, sau đó SSE hoặc SignalR | |
| CSDL, kho file | PostgreSQL, thư mục máy chủ rồi S3 hoặc R2 | |
| Đăng nhập | Tài khoản nội bộ hoặc SSO | Xem phần bảo mật ở mục 10 |

### 15.7. Lộ trình điều chỉnh (W0-W5)

| Bước | Nội dung | Tương ứng giai đoạn cũ |
|------|----------|------------------------|
| **W0** | Web, API, hàng đợi, **worker giả**; Configurator đầu tiên với **schema JSON viết tay cho sản phẩm hộp** (chưa cần DSL); trang job; tải file mẫu | G0 |
| **W1** | Lõi DSL (D0-D2) thay schema viết tay: form, luật và giá sinh từ định nghĩa; công cụ dòng lệnh kiểm tra và chạy thử | D1-D2, G3 một phần |
| **W2** | Worker thật dựng hộp, trả ảnh nhiều góc và STEP | G1 |
| **W3** | Bản vẽ, BOM, PDF, giá, báo cáo; Viewer 3D thật | G2, G3 |
| **W4** | Xem trước tức thì trên web (2D rồi 3D thô) | Canvas ở mục 5b, chuyển sang web |
| **W5** | Product Studio trên web (editor, dry-run, xuất bản phiên bản); sản phẩm máy biến áp theo từng cấp | G4, G5 |

### 15.8. Rủi ro riêng của hướng web

| Rủi ro | Cách giảm |
|--------|-----------|
| Xem trước trên web lệch với model Inventor | Ghi rõ là minh họa, luôn có ảnh thật sau khi dựng, dùng chung kế hoạch dựng để hai bên không lệch logic |
| Phải viết hai bộ sinh hình (Inventor và WebGL) | Giữ kế hoạch dựng ở mức trừu tượng (khối, trụ, quét theo đường), mỗi đích có bộ chuyển riêng; bản đầu chỉ cần sơ đồ 2D |
| Web editor cho DSL tốn công như một dự án riêng | Làm sau cùng; bản đầu soạn file trong repo |
| Dữ liệu thiết kế đi qua cloud (APS) | Cân nhắc glTF nội bộ nếu dữ liệu nhạy cảm; thỏa thuận rõ trước khi đưa file lên APS |
| Phạm vi quá rộng (Configurator, Viewer, Studio, Admin) | Làm Configurator và Orders trước; Studio và Admin là bước sau |
| Chạy luật ở hai nơi (trình duyệt và server) cho kết quả khác nhau | Server luôn kiểm tra lại là bản chuẩn; nếu chạy ở trình duyệt thì dùng cùng thư viện (WebAssembly) |

### 15.9. Việc làm ngay tiếp theo
1. Chốt phạm vi "làm trên web" ở bản đầu: chỉ Configurator và theo dõi đơn, hay có cả Product Studio.
2. Chọn frontend (React hoặc Next.js, hoặc Blazor WebAssembly).
3. Làm **W0**: web, API, worker giả, form hộp từ schema JSON.
4. Chốt cách xem model thật ở bản đầu (ảnh nhiều góc) và để APS Viewer sang bước sau.
