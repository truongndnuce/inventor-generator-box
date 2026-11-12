using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>
/// A high-voltage live-tank circuit breaker, as a simplified model: a concrete foundation (pad and pier), a lattice steel stand (four legs, horizontal braces, top plate),
/// a control cabinet fixed to the stand, and the interrupter: two stacked ribbed insulator columns joined by a collar (grading ring).
/// A single place of definition: the server (form, rule checks) and the add-in (built by Scripts\circuit-breaker.gea) both read from here. Unit: mm.
/// Coordinate system: the pole axis is vertical at X = 0, D = 0; the ground is at 0; the control cabinet is on the -X side.
/// </summary>
public static class CircuitBreakerParams
{
    public const string Code = "circuit-breaker";
    public const string Prefix = "CB_";

    public const string ColumnDiameter = "CB_ColumnDiameter";
    public const string LowerLength = "CB_LowerLength";
    public const string UpperLength = "CB_UpperLength";
    public const string ShedCount = "CB_ShedCount";
    public const string CollarHeight = "CB_CollarHeight";

    public const string StandHeight = "CB_StandHeight";
    public const string StandWidth = "CB_StandWidth";
    public const string LegSize = "CB_LegSize";
    public const string BraceCount = "CB_BraceCount";

    public const string CabWidth = "CB_CabWidth";
    public const string CabDepth = "CB_CabDepth";
    public const string CabHeight = "CB_CabHeight";

    public const string PadWidth = "CB_PadWidth";
    public const string PadHeight = "CB_PadHeight";
    public const string PierWidth = "CB_PierWidth";
    public const string PierHeight = "CB_PierHeight";

    public const string GroupColumn = "Interrupter columns";
    public const string GroupStand = "Support stand";
    public const string GroupCabinet = "Control cabinet";
    public const string GroupFoundation = "Foundation";

    // Geometry constants shared between the validation rules and the builder (the script uses the same values).
    public const double TurretHeight = 150;       // the metal base ring of the lower column
    public const double TurretFactor = 1.2;       // its diameter = 1.2 x the column diameter (must fit on the stand: rule below)
    public const double CollarFactor = 1.35;      // the collar between the two columns = 1.35 x the column diameter
    public const double UpperTurretHeight = 60;   // the ring at the foot of the upper column
    public const double CapHeight = 80;           // the cap on top of the upper column
    public const double Sink = 30;                // parts sink 30 mm into each other so the bodies join
    public const double PlateHeight = 40;         // the top plate of the stand
    public const double CabLowFraction = 0.3;     // the cabinet bottom is at 30 % of the stand height

    public static readonly IReadOnlyList<ParameterDef> All = new[]
    {
        new ParameterDef(ColumnDiameter, "Column diameter (largest shed)", GroupColumn, 420, 200, 900),
        new ParameterDef(LowerLength, "Lower column length", GroupColumn, 1500, 600, 4000),
        new ParameterDef(UpperLength, "Upper column length", GroupColumn, 1500, 600, 4000),
        new ParameterDef(ShedCount, "Sheds per column", GroupColumn, 14, 4, 40) { Unit = "", Integer = true },
        new ParameterDef(CollarHeight, "Collar height", GroupColumn, 120, 60, 300),

        new ParameterDef(StandHeight, "Stand height", GroupStand, 1800, 800, 4000),
        new ParameterDef(StandWidth, "Stand width", GroupStand, 700, 400, 1500),
        new ParameterDef(LegSize, "Leg size", GroupStand, 100, 50, 250),
        new ParameterDef(BraceCount, "Horizontal braces", GroupStand, 4, 0, 10) { Unit = "", Integer = true },

        new ParameterDef(CabWidth, "Cabinet width", GroupCabinet, 600, 300, 1200),
        new ParameterDef(CabDepth, "Cabinet depth", GroupCabinet, 350, 200, 800),
        new ParameterDef(CabHeight, "Cabinet height", GroupCabinet, 800, 400, 1500),

        new ParameterDef(PadWidth, "Pad width", GroupFoundation, 1800, 900, 4000),
        new ParameterDef(PadHeight, "Pad height", GroupFoundation, 250, 100, 800),
        new ParameterDef(PierWidth, "Pier width", GroupFoundation, 1000, 500, 2500),
        new ParameterDef(PierHeight, "Pier height", GroupFoundation, 600, 200, 1500),
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

    /// <summary>Elevation of the top of the pier (the stand stands on it).</summary>
    public static double PierTop(IReadOnlyDictionary<string, double> v) => v[PadHeight] + v[PierHeight];

    /// <summary>Elevation of the top of the stand.</summary>
    public static double StandTop(IReadOnlyDictionary<string, double> v) => PierTop(v) + v[StandHeight];

    /// <summary>Elevation of the highest point: the cap on the upper column.</summary>
    public static double TopHeight(IReadOnlyDictionary<string, double> v) =>
        StandTop(v) + TurretHeight + v[LowerLength] + v[CollarHeight] + (UpperTurretHeight - Sink) + v[UpperLength] + CapHeight;

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

        if (V(PierWidth) > V(PadWidth) - 200 + eps)
            issues.Add(new ValidationIssue("Pier width must be at least 200 mm smaller than the pad width.", new[] { PierWidth, PadWidth }));
        if (V(StandWidth) > V(PierWidth) - 100 + eps)
            issues.Add(new ValidationIssue("Stand width must be at least 100 mm smaller than the pier width.", new[] { StandWidth, PierWidth }));
        if (TurretFactor * V(ColumnDiameter) > V(StandWidth) + eps)
            issues.Add(new ValidationIssue("The column base ring (1.2 x the column diameter) must fit on the stand: reduce the column diameter or widen the stand.", new[] { ColumnDiameter, StandWidth }));
        if (4 * V(LegSize) > V(StandWidth) + eps)
            issues.Add(new ValidationIssue("Leg size must be at most a quarter of the stand width.", new[] { LegSize, StandWidth }));
        if (V(CabDepth) > V(StandWidth) + eps)
            issues.Add(new ValidationIssue("Cabinet depth must not exceed the stand width.", new[] { CabDepth, StandWidth }));
        if (V(CabHeight) > (1 - CabLowFraction) * V(StandHeight) + eps)
            issues.Add(new ValidationIssue("Cabinet height must be at most 70 % of the stand height (it is fixed from 30 % of the stand height upward).", new[] { CabHeight, StandHeight }));

        return issues;
    }
}
