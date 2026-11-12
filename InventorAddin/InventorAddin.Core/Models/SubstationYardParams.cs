using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>
/// The yard of a substation (an internal part used by the substation assembly, not an orderable product): a gravel pad, a concrete walkway along the yard,
/// and two parallel portal gantries (two lattice towers and a crossbeam each). Unit: mm.
/// Coordinate system: X along the yard, D across the yard (centered at 0), the gravel top is at <see cref="GravelHeight"/> above the ground (0).
/// </summary>
public static class SubstationYardParams
{
    public const string Code = "substation-yard";
    public const string Prefix = "YD_";

    public const string Length = "YD_Length";
    public const string Width = "YD_Width";
    public const string GravelHeight = "YD_GravelHeight";
    public const string GantryX = "YD_GantryX";
    public const string TowerHeight = "YD_TowerHeight";
    public const string TowerSize = "YD_TowerSize";

    public const string GroupYard = "Yard";
    public const string GroupGantry = "Gantry";

    public const double GantrySpacing = 4500;   // distance between the two parallel gantries (along X)
    public const double TowerMargin = 2000;     // the towers stand this far inside the long edges of the yard

    public static readonly IReadOnlyList<ParameterDef> All = new[]
    {
        new ParameterDef(Length, "Yard length (X)", GroupYard, 44000, 20000, 120000),
        new ParameterDef(Width, "Yard width (Z)", GroupYard, 30000, 15000, 80000),
        new ParameterDef(GravelHeight, "Gravel thickness", GroupYard, 150, 50, 500),
        new ParameterDef(GantryX, "Gantry position (X, from the yard center)", GroupGantry, 14000, 0, 55000),
        new ParameterDef(TowerHeight, "Tower height", GroupGantry, 12000, 6000, 25000),
        new ParameterDef(TowerSize, "Tower leg size", GroupGantry, 500, 250, 1000),
    };

    public static ParameterDef Get(string name) => All.First(d => d.Name == name);

    public static Dictionary<string, double> Defaults() => All.ToDictionary(d => d.Name, d => d.Default);

    public static Dictionary<string, double> Complete(IReadOnlyDictionary<string, double>? partial)
    {
        var result = Defaults();
        if (partial == null) return result;
        foreach (var d in All)
            if (partial.TryGetValue(d.Name, out var v))
                result[d.Name] = v;
        return result;
    }

    public static double TopHeight(IReadOnlyDictionary<string, double> v) => v[GravelHeight] + v[TowerHeight];

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
        }
        if (!rangeOk) return issues;

        if (V(GantryX) + GantrySpacing > V(Length) / 2 - 1000 + eps)
            issues.Add(new ValidationIssue("Both gantries must stand inside the yard: gantry position + 4500 mm must be at most half the yard length - 1000 mm.", new[] { GantryX, Length }));
        return issues;
    }
}
