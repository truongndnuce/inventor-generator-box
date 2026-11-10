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
