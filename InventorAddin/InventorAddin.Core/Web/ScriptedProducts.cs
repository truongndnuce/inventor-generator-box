using System;
using System.Collections.Generic;
using System.Linq;
using InventorAddin.Models;

namespace InventorAddin.Web;

/// <summary>
/// Products built by a .gea script that ships with the add-in (transformer, gas turbine, generator, circuit breaker, disconnector). Each product is declared once:
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

        new ScriptedProduct(CircuitBreakerParams.Code, "circuit-breaker.gea", "Building circuit breaker",
            "foundation, lattice stand, control cabinet and the two interrupter columns.",
            CircuitBreakerParams.Complete, CircuitBreakerParams.Validate,
            v => new[] { 0, CircuitBreakerParams.PierTop(v), CircuitBreakerParams.StandTop(v), CircuitBreakerParams.TopHeight(v) }),

        new ScriptedProduct(DisconnectorParams.Code, "disconnector.gea", "Building disconnector",
            "posts, frame beams, insulator columns, blade arms and the control cabinet.",
            DisconnectorParams.Complete, DisconnectorParams.Validate,
            v => new[] { 0, v[DisconnectorParams.PostHeight], DisconnectorParams.FrameTop(v), DisconnectorParams.TopHeight(v) }),

        // Internal part of the substation assembly (no add-in and no entry in the server's Products: it is only placed with ADD_COMPONENT).
        new ScriptedProduct(SubstationYardParams.Code, "substation-yard.gea", "Building substation yard",
            "gravel pad, walkway and two portal gantries.",
            SubstationYardParams.Complete, SubstationYardParams.Validate,
            v => new[] { 0, v[SubstationYardParams.GravelHeight], SubstationYardParams.TopHeight(v) - 1000, SubstationYardParams.TopHeight(v) }),
    };

    public static ScriptedProduct? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}
