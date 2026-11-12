using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>
/// A large water-cooled synchronous generator, as a simplified block model:
/// a box-shaped stator body with vertical ribs on both sides, a water cooler box hanging below the body with inlet/outlet pipes, a terminal box and two fans on top,
/// two bearing end covers with pedestals, a rotor shaft running through, an excitation cabinet at the shaft end and a base frame.
/// A single place of definition: the server (form, rule checks) and the add-in (built by Scripts\generator.gea) both read from here. Unit: mm.
/// Coordinate system: the machine axis is X (excitation cabinet at -X, shaft end out at +X), body center at X = 0, base bottom at 0, base top at BaseHeight.
/// </summary>
public static class GeneratorParams
{
    public const string Code = "generator";
    public const string Prefix = "GN_";

    public const string FrameLength = "GN_FrameLength";
    public const string FrameWidth = "GN_FrameWidth";
    public const string FrameHeight = "GN_FrameHeight";
    public const string EndDiameter = "GN_EndDiameter";
    public const string EndLength = "GN_EndLength";
    public const string ShaftDiameter = "GN_ShaftDiameter";
    public const string ShaftExtension = "GN_ShaftExtension";

    public const string RibCount = "GN_RibCount";
    public const string RibThickness = "GN_RibThickness";
    public const string RibExtra = "GN_RibExtra";

    public const string CoolerLength = "GN_CoolerLength";
    public const string CoolerWidth = "GN_CoolerWidth";
    public const string CoolerDepth = "GN_CoolerDepth";
    public const string PipeDiameter = "GN_PipeDiameter";

    public const string CabLength = "GN_CabLength";
    public const string CabWidth = "GN_CabWidth";
    public const string CabHeight = "GN_CabHeight";

    public const string BaseHeight = "GN_BaseHeight";
    public const string BaseWidth = "GN_BaseWidth";
    public const string BaseOverhang = "GN_BaseOverhang";

    public const string GroupFrame = "Stator frame and rotor";
    public const string GroupRibs = "Side ribs";
    public const string GroupCooler = "Water cooler";
    public const string GroupCabinet = "Exciter cabinet";
    public const string GroupBase = "Base frame";

    // Geometry constants shared between the validation rules and the builder (the script uses the same values).
    public const double AxisFraction = 0.55;     // the machine axis is at 55 % of the body height, measured from the body bottom
    public const double CoolerOverlap = 300;     // the cooler box sinks 300 mm into the body so the two blocks join
    public const double PipeStub = 300;          // the water pipe sticks out 300 mm from the face of the cooler box
    public const double PipeBudget = 0.6;        // maximum pipe diameter = 0.6 x the depth of the cooler box (a flange of 1.5 x the pipe still sits on the base)
    public const double EndFraction = 0.8;       // the bearing cover is at most 80 % of the body height
    public const double CabGap = 100;         // gap between the end cover and the excitation cabinet (the shaft passes through this gap)
    public const double SideMargin = 300;        // the base is at least 300 mm wider than the widest part of the machine
    public const double TopBoxHeight = 250;      // the terminal box on top of the body (sinks 30 mm into the body)
    public const double FanHeight = 100;         // the fan cover on the terminal box
    public const double CabTopHeight = 300;      // the control box on the excitation cabinet

    public static readonly IReadOnlyList<ParameterDef> All = new[]
    {
        new ParameterDef(FrameLength, "Frame length", GroupFrame, 4600, 2500, 7000),
        new ParameterDef(FrameWidth, "Frame width", GroupFrame, 2200, 1200, 3800),
        new ParameterDef(FrameHeight, "Frame height", GroupFrame, 2000, 1000, 3500),
        new ParameterDef(EndDiameter, "Bearing housing diameter", GroupFrame, 1300, 600, 2500),
        new ParameterDef(EndLength, "Bearing housing length", GroupFrame, 800, 400, 1500),
        new ParameterDef(ShaftDiameter, "Shaft diameter", GroupFrame, 500, 200, 1000),
        new ParameterDef(ShaftExtension, "Shaft extension (drive end)", GroupFrame, 900, 300, 2000),

        new ParameterDef(RibCount, "Rib count (each side)", GroupRibs, 12, 0, 24) { Unit = "", Integer = true },
        new ParameterDef(RibThickness, "Rib thickness", GroupRibs, 80, 30, 200),
        new ParameterDef(RibExtra, "Rib projection", GroupRibs, 70, 20, 200),

        new ParameterDef(CoolerLength, "Cooler length", GroupCooler, 1500, 800, 3000),
        new ParameterDef(CoolerWidth, "Cooler width", GroupCooler, 1700, 800, 3000),
        new ParameterDef(CoolerDepth, "Cooler depth below frame", GroupCooler, 800, 300, 1500),
        new ParameterDef(PipeDiameter, "Water pipe diameter", GroupCooler, 250, 80, 500),

        new ParameterDef(CabLength, "Cabinet length", GroupCabinet, 1500, 800, 3000),
        new ParameterDef(CabWidth, "Cabinet width", GroupCabinet, 1400, 800, 2500),
        new ParameterDef(CabHeight, "Cabinet height", GroupCabinet, 1800, 800, 3500),

        new ParameterDef(BaseHeight, "Base height", GroupBase, 300, 100, 800),
        new ParameterDef(BaseWidth, "Base width", GroupBase, 3400, 1500, 6000),
        new ParameterDef(BaseOverhang, "Base overhang beyond the ends", GroupBase, 200, 0, 800),
    };

    public static ParameterDef Get(string name) => All.First(d => d.Name == name);

    public static Dictionary<string, double> Defaults() => All.ToDictionary(d => d.Name, d => d.Default);

    /// <summary>Takes the values present in <paramref name="partial"/>, uses the default for missing ones; unknown keys are dropped.</summary>
    public static Dictionary<string, double> Complete(IReadOnlyDictionary<string, double>? partial)
    {
        var result = Defaults();
        if (partial == null) return result;
        foreach (var d in All)
            if (partial.TryGetValue(d.Name, out var v))
                result[d.Name] = v;
        return result;
    }

    /// <summary>Elevation of the body bottom: the cooler box sits between the base and the body bottom.</summary>
    public static double FrameBottom(IReadOnlyDictionary<string, double> v) => v[BaseHeight] + v[CoolerDepth];

    /// <summary>Elevation of the machine axis.</summary>
    public static double AxisHeight(IReadOnlyDictionary<string, double> v) => FrameBottom(v) + AxisFraction * v[FrameHeight];

    /// <summary>Elevation of the highest point of the machine: the fan cover on top of the body, or the top of the control box on the excitation cabinet.</summary>
    public static double TopHeight(IReadOnlyDictionary<string, double> v) =>
        Math.Max(FrameBottom(v) + v[FrameHeight] - 30 + TopBoxHeight - 30 + FanHeight,
            v[BaseHeight] + v[CabHeight] + CabTopHeight);

    /// <summary>Checks value ranges, integers and the geometric constraints. Empty means valid.</summary>
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
        if (!rangeOk) return issues; // the rules below only make sense once every value is within its range

        double frameL = V(FrameLength), frameW = V(FrameWidth), frameH = V(FrameHeight), endD = V(EndDiameter);
        double ribs = V(RibCount);

        if (endD > EndFraction * frameH + eps)
            issues.Add(new ValidationIssue("Bearing housing diameter must be at most 80 % of the frame height.", new[] { EndDiameter, FrameHeight }));
        if (V(ShaftDiameter) > endD - 200 + eps)
            issues.Add(new ValidationIssue("Shaft diameter must be at least 200 mm smaller than the bearing housing diameter.", new[] { ShaftDiameter, EndDiameter }));
        if (V(CoolerLength) > frameL - 400 + eps)
            issues.Add(new ValidationIssue("Cooler length must be at least 400 mm shorter than the frame.", new[] { CoolerLength, FrameLength }));
        if (V(CoolerWidth) > frameW + eps)
            issues.Add(new ValidationIssue("Cooler width must not exceed the frame width.", new[] { CoolerWidth, FrameWidth }));
        if (V(PipeDiameter) > PipeBudget * V(CoolerDepth) + eps)
            issues.Add(new ValidationIssue("Water pipe diameter must be at most 60 % of the cooler depth.", new[] { PipeDiameter, CoolerDepth }));
        if (ribs > 0 && ribs * V(RibThickness) > frameL / 2 + eps)
            issues.Add(new ValidationIssue("The side ribs take more than half of the frame length (reduce count or thickness).", new[] { RibCount, RibThickness, FrameLength }));

        double ribExtra = ribs > 0 ? V(RibExtra) : 0; // with no ribs the rib fields are hidden: the rib rules do not apply
        if (V(BaseWidth) < frameW + 2 * ribExtra + SideMargin - eps)
            issues.Add(new ValidationIssue($"Base width must be at least the frame width + ribs + {SideMargin:0} mm.", new[] { BaseWidth, FrameWidth, RibExtra, RibCount }));
        if (V(BaseWidth) < V(CoolerWidth) + 2 * PipeStub + 100 - eps)
            issues.Add(new ValidationIssue("Base width must be at least the cooler width + 700 mm (the water pipes stick out at the sides).", new[] { BaseWidth, CoolerWidth }));
        if (V(BaseWidth) < V(CabWidth) + 100 - eps)
            issues.Add(new ValidationIssue("Base width must be at least the cabinet width + 100 mm.", new[] { BaseWidth, CabWidth }));

        return issues;
    }
}
