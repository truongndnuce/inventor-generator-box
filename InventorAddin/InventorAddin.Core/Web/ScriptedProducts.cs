using System;
using System.Collections.Generic;
using System.Linq;
using InventorAddin.Models;

namespace InventorAddin.Web;

/// <summary>
/// Sản phẩm dựng bằng script .gea đi kèm add-in (máy biến áp, turbine khí, máy phát). Mỗi sản phẩm khai báo một lần:
/// script, luật kiểm tra dùng chung với server, và các cao độ để ghi chuỗi kích thước trên mặt cắt bản vẽ.
/// Thêm sản phẩm mới = thêm một dòng ở <see cref="All"/> (và một add-in nếu muốn có cửa sổ riêng).
/// </summary>
public sealed record ScriptedProduct(
    string Code,
    string ScriptFile,
    string Stage,
    string StageMessage,
    Func<IReadOnlyDictionary<string, double>?, Dictionary<string, double>> Complete,
    Func<IReadOnlyDictionary<string, double>, IReadOnlyList<ValidationIssue>> Validate,
    Func<IReadOnlyDictionary<string, double>, double[]> Levels)
{
    public static readonly IReadOnlyList<ScriptedProduct> All = new[]
    {
        new ScriptedProduct(TransformerParams.Code, "transformer.gea", "Building transformer",
            "tank, radiators, conservator and bushings.",
            TransformerParams.Complete, TransformerParams.Validate,
            v => new[] { 0, v[TransformerParams.BaseThickness], v[TransformerParams.BaseThickness] + v[TransformerParams.TankHeight],
                         v[TransformerParams.BaseThickness] + v[TransformerParams.TankHeight] + v[TransformerParams.CoverThickness] }),

        new ScriptedProduct(TurbineParams.Code, "turbine.gea", "Building gas turbine",
            "casings, rings, enclosure, combustor cans and skid.",
            TurbineParams.Complete, TurbineParams.Validate,
            v => new[] { 0, v[TurbineParams.BaseHeight],
                         v[TurbineParams.BaseHeight] + TurbineParams.MaxDiameter(v) / 2 + TurbineParams.CradleGap,
                         v[TurbineParams.BaseHeight] + TurbineParams.MaxDiameter(v) + TurbineParams.CradleGap }),

        new ScriptedProduct(GeneratorParams.Code, "generator.gea", "Building generator",
            "base, water cooler, stator frame with ribs, bearing housings, rotor shaft and exciter cabinet.",
            GeneratorParams.Complete, GeneratorParams.Validate,
            v => new[] { 0, v[GeneratorParams.BaseHeight], GeneratorParams.AxisHeight(v), GeneratorParams.TopHeight(v) }),
    };

    public static ScriptedProduct? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}
