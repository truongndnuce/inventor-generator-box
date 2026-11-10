# Kế hoạch triển khai: `INCLUDE lib:` cho script .gea

Thiết kế: `docs/superpowers/specs/2026-11-10-gea-include-design.md`. Mỗi task kết thúc bằng một commit; test chạy bằng `dotnet test be/Cadflow.Api.Tests` (cần PostgreSQL local, xem `docs/database.md`). Add-in Inventor không chạy được ở máy này, chỉ build được.

## Task 1: Bộ mở rộng `GeaIncludes` (Core, thuần văn bản)

**File mới:** `InventorAddin/InventorAddin.Core/Gea/GeaIncludes.cs`; link vào `be/Cadflow.Api/Cadflow.Api.csproj` (đã link `Gea\*.cs` nên tự có). **Test:** `be/Cadflow.Api.Tests/GeaIncludesTests.cs`.

1. Viết test trước (đỏ). Dùng resolver giả `name => dict.GetValueOrDefault(name)`:
   - Mở rộng trong `BEGIN_FORM` chèn thân FORM của thư viện; trong `BEGIN_BUILD` chèn thân BUILD; thư viện thiếu khối đó thì chèn rỗng.
   - Header thư viện (`GEA_VERSION`, `PRODUCT_NAME`, `TARGET`, `PARAM_PREFIX`) không vào kết quả; `PARAM_PREFIX` đi vào `LibPrefixes`.
   - `Lines[i]` ánh xạ đúng `(File, Line)` cho dòng của cha và dòng của thư viện.
   - Lỗi (mỗi lỗi một test, `Diagnostics` có `GEA3001`...): `INCLUDE` ngoài khối; thiếu thư viện; tên có `/`, `\`, `..` hoặc ngoài `^[A-Za-z0-9_-]{1,64}$`; vòng lặp A↔B; include lần hai cùng loại khối; độ sâu > 8; resolver null.
2. Cài đặt:
   ```csharp
   public sealed record GeaSourceLine(string File, int Line);
   public sealed record GeaExpansion(string Text, IReadOnlyList<GeaSourceLine> Lines,
       IReadOnlyList<string> LibPrefixes, IReadOnlyList<GeaDiagnostic> Diagnostics);
   public static class GeaIncludes
   {
       public const int MaxDepth = 8;
       public static GeaExpansion Expand(string text, Func<string, string?>? resolveLib, string fileName = "script");
   }
   ```
   Quét từng dòng, theo dõi khối hiện tại bằng dòng `BEGIN_FORM`/`BEGIN_BUILD` và `END_*` (bỏ qua chú thích `!` và dòng trong chuỗi). Dòng `INCLUDE lib:<tên>` (không phân biệt hoa thường, cho phép chú thích đuôi) được thay bằng thân khối cùng loại của thư viện, đệ quy với tập thư viện đang mở (để bắt vòng) và độ sâu. Thân khối = các dòng giữa `BEGIN_x` và `END_x` của thư viện (không lấy chính hai dòng đó). Mã lỗi `GEA3001`+ cho nhóm include, vị trí = dòng của `INCLUDE` trong file chứa nó.
3. Chạy test xanh, commit `Add GeaIncludes expander`.

## Task 2: Compile mở rộng + tiền tố + ánh xạ dòng

**Sửa:** `Gea/GeaInterpreter.cs` (`GeaScript.Compile`), `Gea/GeaAst.cs` (`GeaProgram`), `Gea/GeaChecker.cs`. **Test:** `GeaIncludeCompileTests.cs`.

1. Test trước:
   - Script cha `PARAM_PREFIX GT_` + `INCLUDE` thư viện `PARAM_PREFIX TR_` (một tham số `TR_X`, một `GT_Y`): `Compile` không lỗi, `Parameters` có cả hai.
   - Tham số lạ (`ZZ_Q`) vẫn bị `GEA2030`.
   - Lỗi trong thư viện có `Message` bắt đầu bằng `tank-lib.gea:` và `Line` là dòng trong thư viện; lỗi ở cha sau chỗ chèn báo đúng dòng gốc của cha.
   - Không truyền resolver và có `INCLUDE` → lỗi `GEA3001`, không ném ngoại lệ.
   - Script không có `INCLUDE` cho kết quả y hệt trước (hồi quy: chạy lại các test Gea hiện có nếu có, và test số tham số của `transformer.gea`).
2. Cài đặt:
   - `GeaProgram` thêm `IReadOnlyList<string> ExtraParamPrefixes { get; init; }` (mặc định rỗng); parser giữ nguyên, `Compile` dùng `with`/gán lại sau khi parse (đổi `init` thành `set` nếu cần).
   - `Compile(string text, Func<string,string?>? resolveLib = null, string fileName = "script")`: gọi `Expand`, parse `expansion.Text`, gán `ExtraParamPrefixes = LibPrefixes`, gộp `expansion.Diagnostics`, sau khi check thì với mỗi chẩn đoán đổi `Line` qua `Lines` và nếu `File != fileName` thì thêm tiền tố `"<File>:<Line>: "` vào `Message`. Sắp xếp như cũ.
   - `GeaScript` thêm `ExpandedText` (văn bản sau mở rộng; bằng `Text` khi không có INCLUDE).
   - Checker: hai chỗ kiểm tra tiền tố (`GeaChecker.cs` quanh dòng 337 và 393) dùng hàm `HasAllowedPrefix(name)` = bắt đầu bằng `ParamPrefix` hoặc bất kỳ tiền tố trong `ExtraParamPrefixes`; dòng 488 (`DeclareHint`) giữ nguyên ý.
3. Test xanh, commit `Compile expands INCLUDE and accepts library prefixes`.

## Task 3: Thư viện `tank-base-cover` và `transformer.gea` dùng nó

**File mới:** `InventorAddin/InventorAddin.Core/Scripts/lib/tank-base-cover.gea`. **Sửa:** `Scripts/transformer.gea`, `InventorAddin.Core.csproj` (copy `Scripts\lib\*.gea` ra output add-in), `be/Cadflow.Api/Cadflow.Api.csproj` (Content link `Scripts\lib\tank-base-cover.gea` → `Scripts\lib\`, `PreserveNewest`). **Test:** `TransformerIncludeEquivalenceTests.cs`.

1. Test trước (đỏ vì chưa có thư viện): đọc `transformer.gea` hiện tại bằng git (`git show HEAD:...` lưu một bản gốc làm fixture `be/Cadflow.Api.Tests/Fixtures/transformer.original.gea`) và bản mới đã `INCLUDE`; so sánh `Compile(...)` hai bản:
   - cùng danh sách tham số (tên, kiểu, mặc định, min, max, nhãn, nhóm),
   - cùng kết quả `EvaluateFormNumbers` cho mặc định và vài bộ giá trị (kể cả vi phạm luật),
   - cùng chuỗi lệnh BUILD (so `ExpandedText` đã chuẩn hóa khoảng trắng và bỏ dòng trống/chú thích).
2. Tách nhóm: khối `BEGIN_GROUP "Tank, base and cover"` (dòng 20–28) thành thân FORM của thư viện; phần BUILD "Base, tank and cover" (đế/thùng/nắp, `hTank`, `hCover`, `hTop` và ba `SET_REF_NAME`) thành thân BUILD. `DECLARE_VARIABLE` các biến đó dời vào thư viện nếu chỉ thư viện dùng, ngược lại để lại ở cha. Thư viện có header `GEA_VERSION 1`, `PRODUCT_NAME`, `PARAM_PREFIX TR_`, `TARGET PART`.
3. `transformer.gea`: thay các khối đó bằng `INCLUDE lib:tank-base-cover` đúng vị trí cũ trong cả FORM và BUILD.
4. Test xanh; commit `Extract tank-base-cover library; transformer.gea uses INCLUDE`.

## Task 4: Server nạp thư viện, lưu bản đã mở rộng

**File mới:** `be/Cadflow.Api/GeaLibraries.cs` (`Resolve(name)` đọc `AppContext.BaseDirectory/Scripts/lib/<tên>.gea`, null nếu không có; tên không hợp lệ trả null). **Sửa:** `be/Cadflow.Api/JobService.cs` (ba chỗ `GeaScript.Compile` ở dòng 30, 81, 115 truyền `GeaLibraries.Resolve`; dòng 60 mở rộng bản bundled trước `scripts.Save`; import lưu `script.ExpandedText` thay cho `clean`). **Test:** `ServerIncludeTests.cs`.

1. Test trước (dùng `TestDb`, ghi thư viện thử vào `Scripts/lib` của thư mục test hoặc dùng `tank-base-cover` thật):
   - `Import` text có `INCLUDE lib:tank-base-cover` trả `form` có tham số `TR_TankLength`; `ScriptStore.Get(scriptId)` không còn chứa `INCLUDE`.
   - Tạo job `transformer` (đường built-in): `ScriptStore.Get(job.ScriptSha256)` không còn `INCLUDE`, và `claim` trả `ScriptText` đã mở rộng.
   - Import script dùng thư viện không tồn tại → 400 kèm `diagnostics`.
2. Cài đặt như trên; `ScriptStore.ReadBundled` giữ nguyên (chỉ đọc), phần mở rộng nằm ở `JobService`. Kiểm tra `Cadflow.Api.csproj` copy `Scripts/lib` ra output khi chạy test.
3. Test xanh, commit `Server expands INCLUDE on import and job creation`.

## Task 5: Add-in mở file có INCLUDE (chỉ build)

**Sửa:** `InventorAddin/InventorAddin.Core/ViewModels/ScriptViewModel.cs` (dòng ~537): `GeaScript.Compile(_scriptText, GeaLibraryResolver.For(directoryOfOpenFile))`. `InventorJobExecutor.cs:112` không đổi (job nhận bản đã mở rộng). **File mới:** `Core/Gea/GeaLibraryResolver.cs`: thử `<thư mục file>/lib/<tên>.gea` và `<thư mục file>/<tên>.gea`, rồi `AppContext.BaseDirectory/Scripts/lib/<tên>.gea`; chỉ nhận tên hợp lệ.

1. Thêm test thuần cho `GeaLibraryResolver` ở `Cadflow.Api.Tests` (nếu file được link) hoặc kiểm tra bằng đọc mã nếu không link được.
2. Build solution add-in nếu máy có SDK/tham chiếu Inventor; nếu không, ghi rõ chưa build được. Commit `Add-in resolves INCLUDE libraries`.

## Task 6: Tài liệu

- `sa-docs/gea-language-spec.md`: thêm mục `INCLUDE lib:` (cú pháp trong khối, biến dùng chung, tiền tố thư viện, lỗi `GEA3xxx`, giới hạn), cập nhật bảng P1 và đánh dấu Q1 đã chốt (dùng chung biến).
- `sa-docs/smartassembly-to-inventor-mapping.md` và `docs/smartassembly-to-inventor-mapping.md`: D6 đã chốt.
- `README.md`: một dòng về `Scripts/lib` và cách thêm thư viện.
- Commit `Document INCLUDE`.

## Nghiệm thu

- `dotnet test be/Cadflow.Api.Tests` toàn bộ xanh (22 test cũ + test mới).
- Chạy backend, đặt job `transformer` qua `POST /api/jobs`: job được tạo, `claim` trả script đã mở rộng.
- Import `.gea` có `INCLUDE` qua `POST /api/gea/import` trả form đúng.
- `git status` sạch, không commit file build hay `.env`.
