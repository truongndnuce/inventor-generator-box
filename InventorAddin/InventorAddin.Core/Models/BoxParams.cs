using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>Định nghĩa một thông số (một nơi duy nhất; UI, preset và adapter đều đọc từ đây). Đơn vị: mm.</summary>
public sealed record ParameterDef(string Name, string Label, string Group, double Default, double Min, double Max)
{
    /// <summary>Đơn vị hiển thị ("mm", "" cho số đếm).</summary>
    public string Unit { get; init; } = "mm";

    /// <summary>Số nguyên (số sứ, số cánh tản nhiệt...): form dùng bước 1 và luật từ chối giá trị lẻ.</summary>
    public bool Integer { get; init; }
}

/// <summary>Một lỗi ràng buộc, kèm danh sách thông số liên quan để UI tô đỏ.</summary>
public sealed record ValidationIssue(string Message, IReadOnlyList<string> Params);

public static class BoxParams
{
    public const string Length = "BX_Length";
    public const string Width = "BX_Width";
    public const string Height = "BX_Height";
    public const string Wall = "BX_Wall";
    public const string Bottom = "BX_Bottom";
    public const string CornerRadius = "BX_CornerRadius";
    public const string LidThickness = "BX_LidThickness";
    public const string LidLip = "BX_LidLip";
    public const string LidClearance = "BX_LidClearance";

    public const string GroupBody = "Box body";
    public const string GroupLid = "Lid";
    public const string Prefix = "BX_";

    public static readonly IReadOnlyList<ParameterDef> All = new[]
    {
        new ParameterDef(Length, "Length", GroupBody, 100, 40, 500),
        new ParameterDef(Width, "Width", GroupBody, 60, 30, 300),
        new ParameterDef(Height, "Body height", GroupBody, 40, 20, 200),
        new ParameterDef(Wall, "Wall thickness", GroupBody, 2, 1, 10),
        new ParameterDef(Bottom, "Bottom thickness", GroupBody, 2, 1, 10),
        new ParameterDef(CornerRadius, "Corner radius", GroupBody, 3, 0, 20),
        new ParameterDef(LidThickness, "Lid thickness", GroupLid, 2, 1, 10),
        new ParameterDef(LidLip, "Lid lip height", GroupLid, 4, 2, 15),
        new ParameterDef(LidClearance, "Lid lip clearance", GroupLid, 0.3, 0, 1),
    };

    public static ParameterDef Get(string name) => All.First(d => d.Name == name);

    public static Dictionary<string, double> Defaults() => All.ToDictionary(d => d.Name, d => d.Default);

    /// <summary>Lấy các giá trị có trong <paramref name="partial"/>, thông số thiếu thì dùng mặc định; khóa lạ bị bỏ.</summary>
    public static Dictionary<string, double> Complete(IReadOnlyDictionary<string, double>? partial)
    {
        var result = Defaults();
        if (partial == null) return result;
        foreach (var d in All)
            if (partial.TryGetValue(d.Name, out var v))
                result[d.Name] = v;
        return result;
    }

    /// <summary>Kiểm tra min/max và các ràng buộc hình học (mục 4 của kế hoạch). Rỗng nghĩa là hợp lệ.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(IReadOnlyDictionary<string, double> values)
    {
        var issues = new List<ValidationIssue>();
        double V(string n) => values.TryGetValue(n, out var x) ? x : Get(n).Default;

        foreach (var d in All)
        {
            var x = V(d.Name);
            if (double.IsNaN(x) || double.IsInfinity(x) || x < d.Min - 1e-9 || x > d.Max + 1e-9)
                issues.Add(new ValidationIssue($"{d.Label} must be between {d.Min:0.##} and {d.Max:0.##} mm.", new[] { d.Name }));
        }

        double len = V(Length), wid = V(Width), hgt = V(Height), wall = V(Wall), bottom = V(Bottom);
        double r = V(CornerRadius), lip = V(LidLip), clr = V(LidClearance);
        double halfMin = Math.Min(len, wid) / 2;
        const double eps = 1e-9;

        if (wall >= halfMin - eps)
            issues.Add(new ValidationIssue("Wall thickness must be less than half of the shorter side of the box.", new[] { Wall, Length, Width }));
        if (wall >= hgt - bottom - eps)
            issues.Add(new ValidationIssue("Wall thickness must be less than (height - bottom thickness).", new[] { Wall, Height, Bottom }));
        if (bottom >= hgt - eps)
            issues.Add(new ValidationIssue("Bottom thickness must be less than the body height.", new[] { Bottom, Height }));
        if (r > halfMin + eps)
            issues.Add(new ValidationIssue("Corner radius must not exceed half of the shorter side of the box.", new[] { CornerRadius, Length, Width }));
        // Addition to the plan: inner corner radius of the body and of the lid lip must be > 0 so Fillet/Shell do not fail.
        if (r <= wall + clr + eps)
            issues.Add(new ValidationIssue("Corner radius must be greater than (wall thickness + lid clearance) so the inner corners and lid lip can be rounded.", new[] { CornerRadius, Wall, LidClearance }));
        if (lip >= hgt - bottom - eps)
            issues.Add(new ValidationIssue("Lid lip must be shorter than (height - bottom thickness) so it does not touch the bottom.", new[] { LidLip, Height, Bottom }));
        if (clr >= wall - eps)
            issues.Add(new ValidationIssue("Lid clearance must be less than the wall thickness.", new[] { LidClearance, Wall }));

        return issues;
    }
}
