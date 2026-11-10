using System.Text.RegularExpressions;
using InventorAddin.Gea;

namespace Cadflow.Api;

public sealed record ComponentParam(string Name, string Label, string? Group, string Unit, string Type, bool Integer, object Default, double? Min, double? Max, bool Required);

public sealed record ComponentVar(string Name, string Type);

/// <param name="Description">Các dòng chú thích đầu file (bỏ dấu ! và dòng gạch), giữ dòng trống giữa các đoạn.</param>
/// <param name="Summary">Đoạn đầu của <paramref name="Description"/> trên một dòng.</param>
/// <param name="SharedVariables">Biến khai báo trong khối BUILD của thư viện: script cha dùng chung được sau INCLUDE.</param>
/// <param name="Errors">Lỗi biên dịch thư viện (rỗng nếu tốt); thư viện lỗi vẫn được liệt kê.</param>
public sealed record ComponentInfo(string Name, string Title, string Summary, string Description, IReadOnlyList<string> Prefixes,
    IReadOnlyList<ComponentVar> SharedVariables, IReadOnlyList<ComponentParam> Parameters, IReadOnlyList<string> Errors);

/// <summary>Kết quả sinh script khởi tạo: <see cref="Text"/> khi thành công, ngược lại <see cref="Status"/> (400/404) và <see cref="Error"/>.</summary>
public sealed record StarterResult(string? Text, int Status, string? Error);

/// <summary>
/// Danh mục component (thư viện <c>Scripts/lib/*.gea</c>) cho trang Components: mô tả, tham số, biến dùng chung và file gốc.
/// Chỉ đọc. Tên phải hợp lệ và có trong danh sách trước khi đọc file, nên không thể đọc ra ngoài thư mục thư viện.
/// </summary>
public sealed partial class ComponentCatalog
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^\s*DECLARE_VARIABLE\s+(?<type>\w+)\s+(?<name>\w+)")]
    private static partial Regex DeclareVar();

    [GeneratedRegex(@"^\s*DECLARE_REFERENCE\s+(?<name>\w+)")]
    private static partial Regex DeclareRef();

    private readonly Func<IEnumerable<string>> _names;
    private readonly Func<string, string?> _read;

    public ComponentCatalog() : this(GeaLibraries.Names, GeaLibraries.Resolve) { }

    public ComponentCatalog(Func<IEnumerable<string>> names, Func<string, string?> read)
    {
        _names = names;
        _read = read;
    }

    public IReadOnlyList<ComponentInfo> List() =>
        _names().Distinct().OrderBy(n => n, StringComparer.Ordinal).Select(Get).Where(c => c != null).Select(c => c!).ToList();

    /// <summary>Mô tả một component; null nếu tên không hợp lệ hoặc không có.</summary>
    public ComponentInfo? Get(string name) => Raw(name) is { } text ? Describe(name, text) : null;

    /// <summary>Nội dung file thư viện gốc; null nếu tên không hợp lệ hoặc không có.</summary>
    public string? Raw(string name) =>
        ValidName().IsMatch(name) && _names().Contains(name) ? _read(name) : null;

    /// <summary>Script khởi tạo (có chú thích từng bước) dùng component này; xem <see cref="ComponentStarter"/>.</summary>
    public StarterResult Starter(string name, string? productName, string? prefix)
    {
        productName = string.IsNullOrWhiteSpace(productName) ? ComponentStarter.DefaultProductName : productName.Trim();
        prefix = string.IsNullOrWhiteSpace(prefix) ? ComponentStarter.DefaultPrefix : prefix.Trim();
        if (Get(name) is not { } info) return new(null, 404, $"Unknown component '{name}'.");
        if (ComponentStarter.Validate(productName, prefix) is { } bad) return new(null, 400, bad);
        if (info.Errors.Count > 0) return new(null, 400, $"The component '{name}' has errors, so a starter script cannot be generated: {info.Errors[0]}");
        return new(ComponentStarter.Build(info, productName, prefix), 200, null);
    }

    private ComponentInfo Describe(string name, string text)
    {
        var (description, summary) = LeadingComment(text);
        var script = GeaScript.Compile(text, _read, name + ".gea");
        var errors = script.Diagnostics.Where(d => d.Severity == GeaSeverity.Error)
            .Select(d => $"{d.Code} (line {d.Line}): {d.Message}").ToList();

        var prefixes = new List<string>();
        if (!string.IsNullOrEmpty(script.Program.ParamPrefix)) prefixes.Add(script.Program.ParamPrefix);
        prefixes.AddRange(script.Program.ExtraParamPrefixes.Where(p => !prefixes.Contains(p)));

        var parameters = new List<ComponentParam>();
        if (errors.Count == 0)
        {
            foreach (var f in script.EvaluateFormNumbers(null).Fields)
            {
                parameters.Add(new ComponentParam(f.Name, f.Label, f.Group, f.Unit, f.Type.ToString(), f.Type == GeaType.Integer,
                    f.Kind switch
                    {
                        GeaFieldKind.Text or GeaFieldKind.Radio => f.Default.Str ?? "",
                        GeaFieldKind.Checkbox => f.Default.Bool ? 1 : 0,
                        _ => GeaForms.Round(f.DisplayNumber(f.Default)),
                    },
                    f.Min.HasValue ? GeaForms.Round(f.Min.Value / f.UnitFactor) : null,
                    f.Max.HasValue ? GeaForms.Round(f.Max.Value / f.UnitFactor) : null,
                    f.Required));
            }
        }

        return new ComponentInfo(name, string.IsNullOrWhiteSpace(script.Program.ProductName) ? name : script.Program.ProductName,
            summary, description, prefixes, SharedVariables(text), parameters, errors);
    }

    /// <summary>Chú thích đầu file: bỏ dấu `!`, bỏ dòng toàn dấu `=`; đoạn đầu (đến dòng trống) làm tóm tắt.</summary>
    private static (string Description, string Summary) LeadingComment(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) { if (lines.Count == 0) continue; break; }
            if (!t.StartsWith('!')) break;
            var c = t.TrimStart('!').Trim();
            if (c.Length > 0 && c.All(ch => ch == '=')) continue;
            lines.Add(c);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var summary = string.Join(" ", lines.TakeWhile(l => l.Length > 0));
        return (string.Join("\n", lines), summary);
    }

    private static IReadOnlyList<ComponentVar> SharedVariables(string text)
    {
        var vars = new List<ComponentVar>();
        var inBuild = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t == "BEGIN_BUILD") { inBuild = true; continue; }
            if (t == "END_BUILD") break;
            if (!inBuild) continue;
            if (DeclareVar().Match(raw) is { Success: true } v) vars.Add(new ComponentVar(v.Groups["name"].Value, v.Groups["type"].Value.ToUpperInvariant()));
            else if (DeclareRef().Match(raw) is { Success: true } r) vars.Add(new ComponentVar(r.Groups["name"].Value, "REFERENCE"));
        }
        return vars;
    }
}

/// <summary>
/// Sinh script khởi tạo cho một component: khung có hai dòng INCLUDE (FORM và BUILD), chỗ thêm thông số và bước dựng riêng,
/// và chú thích tiếng Việt giải thích từng bước. Khung biên dịch không lỗi và Import được mà chưa sửa gì.
/// </summary>
public static partial class ComponentStarter
{
    public const string DefaultProductName = "New product";
    public const string DefaultPrefix = "MY_";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,15}_$")]
    private static partial Regex ValidPrefix();

    /// <summary>Thông báo lỗi nếu tên sản phẩm hoặc tiền tố không dùng được (đi thẳng vào chuỗi và tên thông số của script).</summary>
    public static string? Validate(string productName, string prefix)
    {
        if (productName.Length is 0 or > 80) return "productName must be 1-80 characters.";
        if (productName.Any(c => c is '"' or '\\' || char.IsControl(c))) return "productName must not contain quotes, backslashes or line breaks.";
        if (!ValidPrefix().IsMatch(prefix)) return "prefix must start with a letter, use letters and digits only, and end with '_' (for example MY_).";
        return null;
    }

    public static string Build(ComponentInfo c, string productName, string prefix)
    {
        var sb = new System.Text.StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');

        var parameters = Wrap(string.Join(", ", c.Parameters.Select(p => $"{p.Name} ({p.Label})")), 100);
        var lengthVar = c.SharedVariables.LastOrDefault(v => v.Type == "LENGTH"); // thường là cao độ trên cùng (hTop)
        var vars = c.SharedVariables.Count == 0 ? "(component này không có biến dùng chung)" : string.Join(", ", c.SharedVariables.Select(v => v.Name));
        var example = prefix + "Example";

        L("! ============================================================================");
        L($"! Script khởi tạo: dùng component \"{c.Title}\" (thư viện {c.Name}).");
        L("! Tạo bởi trang Components của CAD Automation. Sửa file này, rồi Import lại để đặt hàng.");
        L("!");
        L("! Cách đọc file:");
        L("!   - Phần đầu (GEA_VERSION ... TARGET) khai báo sản phẩm.");
        L("!   - BEGIN_FORM ... END_FORM là các ô nhập trên form đặt hàng.");
        L("!   - BEGIN_BUILD ... END_BUILD là các bước dựng model trong Inventor.");
        L("!   - Dòng bắt đầu bằng ! là chú thích, được bỏ qua khi chạy.");
        L("! ============================================================================");
        L();
        L("! Bước 1: phiên bản ngôn ngữ .gea (hiện là 1). Bắt buộc, phải là lệnh đầu tiên của file.");
        L("GEA_VERSION 1");
        L("! Bước 2: tên sản phẩm, hiện trên web và trong danh sách đơn.");
        L($"PRODUCT_NAME \"{productName}\"");
        L($"! Bước 3: tiền tố của thông số do bạn thêm (ví dụ {example}). Thông số của component giữ tiền tố của nó");
        L($"! ({string.Join(", ", c.Prefixes)}); cả hai loại tiền tố đều hợp lệ.");
        L($"PARAM_PREFIX {prefix}");
        L("! Bước 4: loại document cần dựng. PART là một Part (.ipt); web hiện chỉ nhận PART.");
        L("TARGET PART");
        L();
        L("BEGIN_FORM");
        L();
        L($"  ! Bước 5: chèn nhóm thông số của component. Dòng này được thay bằng phần FORM của thư viện {c.Name},");
        L("  ! gồm các thông số:");
        foreach (var w in parameters) L("  !   " + w);
        L($"  INCLUDE lib:{c.Name}");
        L();
        L("  ! Bước 6: thêm thông số của riêng bạn tại đây. Mỗi dòng USER_INPUT_PARAM khai báo một ô nhập");
        L("  ! (tên, kiểu và đơn vị, giá trị mặc định, MIN/MAX, LABEL). Bỏ dấu ! đầu dòng dưới đây để bật ví dụ:");
        L($"  !   USER_INPUT_PARAM {example} LENGTH 100 mm MIN 10 MAX 500 LABEL \"Example\"");
        L();
        L("END_FORM");
        L();
        L("BEGIN_BUILD");
        L();
        L($"  ! Bước 7: chèn các bước dựng của component. Dòng này được thay bằng phần BUILD của thư viện {c.Name}.");
        L("  ! Sau dòng này bạn dùng được các biến dùng chung (đừng khai báo lại):");
        L($"  !   {vars}");
        L($"  INCLUDE lib:{c.Name}");
        L();
        L("  ! Bước 8: thêm bước dựng của riêng bạn tại đây. Ví dụ dưới đây dựng một khối hộp bằng builder Block:");
        L("  ! SizeX, SizeD, Height là kích thước, FEATURE_OUT nhận solid body vừa tạo, SET_REF_NAME đặt tên cho nó.");
        if (lengthVar != null)
            L($"  ! Thêm FEATURE_IN \"Elevation\" {lengthVar.Name} nếu muốn đặt khối ở cao độ của biến {lengthVar.Name} (từ component).");
        L("  ! Bỏ dấu ! đầu các dòng dưới đây để bật ví dụ:");
        L("  !   DECLARE_REFERENCE myBody");
        L("  !   CREATE_FEATURE builder:Block");
        L("  !     FEATURE_IN \"SizeX\"  200 mm");
        L("  !     FEATURE_IN \"SizeD\"  200 mm");
        L("  !     FEATURE_IN \"Height\" 100 mm");
        L("  !     FEATURE_OUT myBody \"Body\"");
        L("  !   END_CREATE_FEATURE");
        L($"  !   SET_REF_NAME myBody \"{example}\"");
        L();
        L("END_BUILD");
        return sb.ToString();
    }

    /// <summary>Ngắt chuỗi thành các dòng không dài quá <paramref name="width"/> ký tự, ngắt sau dấu phẩy.</summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var part in text.Split(", "))
        {
            var piece = cur.Length == 0 ? part : cur + ", " + part;
            if (piece.Length > width && cur.Length > 0) { lines.Add(cur + ","); cur = part; }
            else cur = piece;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }
}
