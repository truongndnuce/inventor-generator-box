using System;
using System.Collections.Generic;
using System.Linq;
using InventorAddin.Models;

namespace InventorAddin.Web;

/// <summary>
/// Products built by a .gea script that ships with the add-in (transformer, gas turbine, generator). Each product is declared once:
/// the script, the validation rules shared with the server, and the elevations used to write the dimension string on the drawing section.
/// Adding a product = adding one line in <see cref="All"/> (and an add-in if it should have a window of its own).
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
