# Thiết kế: `INCLUDE lib:` để dùng lại component trong script .gea

Ngày: 2026-11-10. Trạng thái: đã duyệt hướng thiết kế trong chat.

## Mục tiêu

Dùng lại một component (ví dụ nhóm "Tank, base and cover" của `transformer.gea`: form + bước dựng Base/Tank/Cover) trong script .gea khác, mà không copy mã. Sửa thư viện thì mọi script dùng nó đều đổi. Đây là P1 `INCLUDE lib:<tên>` trong `sa-docs/gea-language-spec.md` và trả lời Q1/D6: **dùng chung biến với script cha**.

## Ngoài phạm vi

Thư viện do người dùng tải lên web, chọn component bằng giao diện, namespace (`AS Tank`), chèn part `.ipt` vào assembly (`ADD_COMPONENT`), include lồng nhau quá 8 tầng.

## Cách làm: mở rộng văn bản trước lexer

Một bước tiền xử lý thay mỗi dòng `INCLUDE lib:<tên>` bằng nội dung tương ứng của thư viện. Lexer, parser, checker và interpreter không đổi (trừ phần tiền tố ở mục 3). Cách này được chọn thay vì include trên AST vì rủi ro thấp hơn nhiều, và thay vì copy khối mã vì sửa thư viện phải lan được.

### 1. Cú pháp

Thư viện là file `Scripts/lib/<tên>.gea`: có header (`GEA_VERSION`, `PARAM_PREFIX`, ...), khối `BEGIN_FORM ... END_FORM` và khối `BEGIN_BUILD ... END_BUILD` (cả hai tùy chọn).

Script cha ghi `INCLUDE lib:<tên>` **bên trong** một khối:

```
BEGIN_FORM
  INCLUDE lib:tank-base-cover      ! chèn thân FORM của thư viện (nhóm tham số, ASSERT)
  BEGIN_GROUP "Radiators" ...
END_FORM

BEGIN_BUILD
  INCLUDE lib:tank-base-cover      ! chèn thân BUILD của thư viện
  ... cánh tản nhiệt, bình dầu ...
END_BUILD
```

Trong `BEGIN_FORM` dòng này chèn thân khối FORM của thư viện, trong `BEGIN_BUILD` chèn thân khối BUILD. Vị trí và thứ tự do script cha quyết định. Thư viện không có khối tương ứng thì chèn không gì cả. `INCLUDE` ngoài khối là lỗi.

### 2. Biến dùng chung

Nội dung chèn thẳng vào script cha, thấy và đặt được mọi biến. Khai báo trùng tên (tham số, biến) do checker hiện có báo lỗi. `DECLARE_VARIABLE` trong thư viện nằm trong phạm vi BUILD của cha.

### 3. Tiền tố tham số

Header của thư viện không được chèn (chỉ lấy `PARAM_PREFIX`). `GeaProgram` có thêm `ExtraParamPrefixes`; checker (`GEA2030` ở `GeaChecker.cs:337` và `:393`) chấp nhận tên bắt đầu bằng tiền tố của cha **hoặc** một tiền tố trong danh sách. Thư viện đầu tiên là `tank-base-cover` với `PARAM_PREFIX TR_` (tên tham số giữ nguyên), và `transformer.gea` đổi sang dùng `INCLUDE`. `TransformerParams.cs`, job cũ và form không đổi.

### 4. API (Core, không phụ thuộc Inventor)

```csharp
public sealed record GeaExpansion(string Text, IReadOnlyList<GeaSourceLine> Lines, IReadOnlyList<string> LibPrefixes, IReadOnlyList<GeaDiagnostic> Diagnostics);
public sealed record GeaSourceLine(string File, int Line);          // dòng của văn bản đã mở rộng -> (file, dòng gốc)
public static class GeaIncludes { public static GeaExpansion Expand(string text, Func<string, string?> resolveLib); }
```

`GeaInterpreter.Compile(string text)` thêm tham số `Func<string,string?>? resolveLib = null`; có `INCLUDE` mà không có resolver thì báo lỗi. Compile gọi `Expand`, parse bản mở rộng, gắn `LibPrefixes`, rồi đổi `Line` của mọi chẩn đoán qua `Lines`. Lỗi trong thư viện có tên file (`tank-base-cover.gea:23`).

### 4a. Bảo vệ

- Tên thư viện khớp `^[A-Za-z0-9_-]{1,64}$`, không có đường dẫn hay `..`.
- Độ sâu tối đa 8 và phát hiện vòng (A include B include A).
- Một thư viện chỉ được include một lần cho mỗi loại khối (lần hai là lỗi, vì sẽ khai báo trùng).
- Thư viện không tìm thấy là lỗi có dòng, không phải ngoại lệ.

### 5. Nơi nạp thư viện

- **Server**: `Scripts/lib/*.gea` được copy ra thư mục chạy (giống `transformer.gea`, link từ `InventorAddin.Core/Scripts/lib`). Resolver đọc `AppContext.BaseDirectory/Scripts/lib/<tên>.gea`. Khi import script hoặc tạo job từ script có `INCLUDE`, server mở rộng **trước** khi lưu: `ScriptStore` lưu và worker nhận **bản đã mở rộng**, nên worker Inventor không cần thư viện, và mã băm tính trên bản đã mở rộng.
- **Add-in**: resolver tìm trong `Scripts/lib` đi kèm add-in, rồi trong thư mục của file đang mở.
- **Export .gea** trên web trả bản đã mở rộng (không giữ dòng `INCLUDE`). File tự chạy được ở bất kỳ đâu.

### 6. Tương thích

`transformer.gea` sau khi đổi phải cho **cùng** danh sách tham số, giá trị mặc định, nhãn, luật `MIN/MAX/ASSERT` và cùng chuỗi lệnh BUILD như bản không dùng `INCLUDE`. Đây là điều kiện nghiệm thu chính.

## Kiểm thử

Trong `be/Cadflow.Api.Tests` (server đã link mã Gea):

1. Mở rộng FORM và BUILD đúng khối; thư viện thiếu khối thì chèn rỗng.
2. `transformer.gea` dùng `INCLUDE` tương đương bản gốc (so danh sách tham số, mặc định, nhãn, kết quả validate với vài bộ giá trị, các lệnh BUILD).
3. Lỗi: `INCLUDE` ngoài khối, thư viện không có, tên chứa `/` hoặc `..`, vòng lặp, include lần hai, vượt độ sâu, không có resolver.
4. Ánh xạ dòng: lỗi trong thư viện báo tên file và dòng của thư viện; lỗi ở script cha báo đúng dòng gốc dù có chèn phía trên.
5. Tiền tố: script có prefix khác (ví dụ `GT_`) include thư viện `TR_` thì hợp lệ; tham số lạ vẫn bị `GEA2030`.
6. Server: import script có `INCLUDE` cho form đúng và lưu bản đã mở rộng; tạo job sinh `WorkItem.ScriptText` không còn `INCLUDE`.

## Tài liệu

Cập nhật `sa-docs/gea-language-spec.md` (mục INCLUDE, đóng Q1, mục P1) và `docs/smartassembly-to-inventor-mapping.md` (D6 đã chốt).

## Rủi ro

- Add-in Inventor không thể build/test ở máy này (không có Inventor). Phần add-in (resolver theo thư mục) chỉ build được, không chạy được; logic mở rộng nằm trong Core nên được test ở server.
- Tham số `TR_` xuất hiện trong script của sản phẩm khác có thể gây nhầm. Đổi tên sang tiền tố trung tính cần đổi `TransformerParams.cs` và job cũ nên để lại sau.
