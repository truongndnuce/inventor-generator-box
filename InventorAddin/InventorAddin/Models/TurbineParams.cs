using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>
/// Turbine khí công nghiệp (kiểu máy aero-derivative như ảnh tham chiếu), mô hình hình khối đơn giản hóa:
/// cửa hút + vỏ máy nén có các vành gân, buồng đốt với vòng ống đốt, tầng turbine, cửa xả, hộp bao (enclosure) trên cửa hút, đế skid.
/// Một nơi định nghĩa duy nhất: server (form, kiểm tra luật) và add-in (dựng bằng Scripts\turbine.gea) đều đọc từ đây. Đơn vị: mm.
/// Hệ tọa độ: trục máy là X (cửa hút ở -X, cửa xả ở +X), tâm máy ở gốc, mặt trên skid ở BaseHeight, đáy skid ở 0.
/// </summary>
public static class TurbineParams
{
    public const string Code = "gas-turbine";
    public const string Prefix = "GT_";

    public const string InletDiameter = "GT_InletDiameter";
    public const string InletLength = "GT_InletLength";
    public const string CompDiameter = "GT_CompDiameter";
    public const string CompLength = "GT_CompLength";
    public const string CombDiameter = "GT_CombDiameter";
    public const string CombLength = "GT_CombLength";
    public const string TurbDiameter = "GT_TurbDiameter";
    public const string TurbLength = "GT_TurbLength";
    public const string ExhDiameter = "GT_ExhDiameter";
    public const string ExhLength = "GT_ExhLength";

    public const string EncLength = "GT_EncLength";
    public const string EncWidth = "GT_EncWidth";
    public const string EncHeight = "GT_EncHeight";
    public const string EncStart = "GT_EncStart";

    public const string FlangeCount = "GT_FlangeCount";
    public const string FlangeThickness = "GT_FlangeThickness";
    public const string FlangeExtra = "GT_FlangeExtra";

    public const string CanCount = "GT_CanCount";
    public const string CanDiameter = "GT_CanDiameter";
    public const string CanOffset = "GT_CanOffset";

    public const string BaseHeight = "GT_BaseHeight";
    public const string BaseWidth = "GT_BaseWidth";
    public const string BaseOverhang = "GT_BaseOverhang";

    public const string GroupCasing = "Casing sections";
    public const string GroupEnclosure = "Enclosure";
    public const string GroupRings = "Compressor rings";
    public const string GroupCans = "Combustor cans";
    public const string GroupSkid = "Skid";

    // Hằng số hình học dùng chung giữa luật kiểm tra và bộ dựng (script đặt cùng giá trị).
    public const double ShaftDiameter = 320;
    public const double ShaftLength = 600;
    public const double InletFlangeExtra = 300;   // vành đầu cửa hút lớn hơn vỏ 300 mm (đường kính)
    public const double InletFlangeThickness = 80;
    public const double ManifoldThickness = 60;   // hai vòng góp ở hai đầu buồng đốt
    public const double CradleGap = 50;           // trục máy cao hơn đỉnh skid: maxD/2 + 50
    public const double SideMargin = 300;         // skid rộng hơn vỏ lớn nhất ít nhất 300 mm

    public static readonly IReadOnlyList<ParameterDef> All = new[]
    {
        new ParameterDef(InletDiameter, "Inlet casing diameter", GroupCasing, 2200, 1200, 3500),
        new ParameterDef(InletLength, "Inlet casing length", GroupCasing, 1100, 600, 2000),
        new ParameterDef(CompDiameter, "Compressor casing diameter", GroupCasing, 1700, 1000, 3000),
        new ParameterDef(CompLength, "Compressor casing length", GroupCasing, 1800, 800, 3000),
        new ParameterDef(CombDiameter, "Combustor casing diameter", GroupCasing, 1500, 800, 3000),
        new ParameterDef(CombLength, "Combustor casing length", GroupCasing, 1400, 800, 2500),
        new ParameterDef(TurbDiameter, "Turbine casing diameter", GroupCasing, 1800, 1000, 3000),
        new ParameterDef(TurbLength, "Turbine casing length", GroupCasing, 1300, 600, 2500),
        new ParameterDef(ExhDiameter, "Exhaust casing diameter", GroupCasing, 2300, 1200, 3500),
        new ParameterDef(ExhLength, "Exhaust casing length", GroupCasing, 1100, 600, 2000),

        new ParameterDef(EncLength, "Enclosure length", GroupEnclosure, 1700, 800, 3000),
        new ParameterDef(EncWidth, "Enclosure width", GroupEnclosure, 1500, 800, 2500),
        new ParameterDef(EncHeight, "Enclosure height", GroupEnclosure, 1000, 400, 2000),
        new ParameterDef(EncStart, "Enclosure start from inlet end", GroupEnclosure, 300, 0, 1500),

        new ParameterDef(FlangeCount, "Ring count", GroupRings, 6, 0, 12) { Unit = "", Integer = true },
        new ParameterDef(FlangeThickness, "Ring thickness", GroupRings, 40, 15, 120),
        new ParameterDef(FlangeExtra, "Ring radial height", GroupRings, 100, 20, 150),

        new ParameterDef(CanCount, "Can count (multiple of 4)", GroupCans, 8, 4, 16) { Unit = "", Integer = true },
        new ParameterDef(CanDiameter, "Can diameter", GroupCans, 140, 60, 300),
        new ParameterDef(CanOffset, "Can clearance from casing", GroupCans, 120, 40, 300),

        new ParameterDef(BaseHeight, "Skid height", GroupSkid, 250, 100, 600),
        new ParameterDef(BaseWidth, "Skid width", GroupSkid, 3200, 1500, 5000),
        new ParameterDef(BaseOverhang, "Skid overhang beyond the ends", GroupSkid, 200, 0, 600),
    };

    public static ParameterDef Get(string name) => All.First(d => d.Name == name);

    public static Dictionary<string, double> Defaults() => All.ToDictionary(d => d.Name, d => d.Default);

    /// <summary>Lấy các giá trị có trong <paramref name="partial"/>, thiếu thì dùng mặc định; khóa lạ bị bỏ.</summary>
    public static Dictionary<string, double> Complete(IReadOnlyDictionary<string, double>? partial)
    {
        var result = Defaults();
        if (partial == null) return result;
        foreach (var d in All)
            if (partial.TryGetValue(d.Name, out var v))
                result[d.Name] = v;
        return result;
    }

    public static double MaxDiameter(IReadOnlyDictionary<string, double> v) =>
        new[] { v[InletDiameter], v[CompDiameter], v[CombDiameter], v[TurbDiameter], v[ExhDiameter] }.Max();

    public static double TotalLength(IReadOnlyDictionary<string, double> v) =>
        v[InletLength] + v[CompLength] + v[CombLength] + v[TurbLength] + v[ExhLength];

    /// <summary>Kiểm tra khoảng giá trị, số nguyên và các ràng buộc hình học. Rỗng nghĩa là hợp lệ.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(IReadOnlyDictionary<string, double> values)
    {
        var issues = new List<ValidationIssue>();
        double V(string n) => values.TryGetValue(n, out var x) ? x : Get(n).Default;
        const double eps = 1e-9;

        bool rangeOk = true;
        foreach (var d in All)
        {
            var x = V(d.Name);
            if (double.IsNaN(x) || double.IsInfinity(x) || x < d.Min - eps || x > d.Max + eps)
            {
                rangeOk = false;
                var unit = d.Unit.Length > 0 ? " " + d.Unit : "";
                issues.Add(new ValidationIssue($"{d.Label} must be between {d.Min:0.##} and {d.Max:0.##}{unit}.", new[] { d.Name }));
            }
            else if (d.Integer && Math.Abs(x - Math.Round(x)) > eps)
            {
                rangeOk = false;
                issues.Add(new ValidationIssue($"{d.Label} must be a whole number.", new[] { d.Name }));
            }
        }
        if (!rangeOk) return issues; // các luật dưới đây chỉ có nghĩa khi từng giá trị đã nằm trong khoảng

        var full = Complete(values);
        double maxD = MaxDiameter(full);
        double canN = V(CanCount), canD = V(CanDiameter), canOff = V(CanOffset), combD = V(CombDiameter);

        if (Math.Abs(canN % 4) > eps)
            issues.Add(new ValidationIssue("Can count must be a multiple of 4 (the cans are placed symmetrically around the axis).", new[] { CanCount }));

        if (V(EncStart) + V(EncLength) > V(InletLength) + V(CompLength) + eps)
            issues.Add(new ValidationIssue("The enclosure must end before the combustor (start + length must be at most inlet length + compressor length).",
                new[] { EncStart, EncLength, InletLength, CompLength }));

        if (V(BaseWidth) < maxD + SideMargin - eps)
            issues.Add(new ValidationIssue($"Skid width must be at least the largest casing diameter + {SideMargin:0} mm.", new[] { BaseWidth, InletDiameter, CompDiameter, CombDiameter, TurbDiameter, ExhDiameter }));
        if (V(BaseWidth) < V(EncWidth) + 100 - eps)
            issues.Add(new ValidationIssue("Skid width must be at least the enclosure width + 100 mm.", new[] { BaseWidth, EncWidth }));

        double ringRadius = combD / 2 + canOff + canD / 2;
        if (canN * canD * 1.1 > 2 * Math.PI * ringRadius + eps)
            issues.Add(new ValidationIssue("The combustor cans overlap each other (reduce count or diameter, or increase clearance).", new[] { CanCount, CanDiameter, CanOffset, CombDiameter }));
        if (combD + 2 * canOff + 2 * canD > V(BaseWidth) + eps)
            issues.Add(new ValidationIssue("The combustor can ring is wider than the skid.", new[] { CombDiameter, CanOffset, CanDiameter, BaseWidth }));

        return issues;
    }
}
