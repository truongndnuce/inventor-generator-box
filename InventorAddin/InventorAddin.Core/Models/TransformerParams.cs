using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>
/// A simplified power transformer (tank + base + cover, radiators, bushings, conservator), a block model following the reference picture.
/// A single place of definition: the server (form, rule checks) and the add-in (build) both read from here. Unit: mm.
/// Coordinate system: tank center at the XY origin, base sitting on z = 0, the long axis of the tank is X, radiators on the +Y side, conservator at the -X end.
/// </summary>
public static class TransformerParams
{
    public const string Code = "transformer";
    public const string Prefix = "TR_";

    public const string TankLength = "TR_TankLength";
    public const string TankWidth = "TR_TankWidth";
    public const string TankHeight = "TR_TankHeight";
    public const string BaseThickness = "TR_BaseThickness";
    public const string BaseOverhang = "TR_BaseOverhang";
    public const string CoverThickness = "TR_CoverThickness";
    public const string CoverOverhang = "TR_CoverOverhang";

    public const string RadiatorCount = "TR_RadiatorCount";
    public const string RadiatorWidth = "TR_RadiatorWidth";
    public const string RadiatorDepth = "TR_RadiatorDepth";
    public const string RadiatorHeight = "TR_RadiatorHeight";
    public const string RadiatorGap = "TR_RadiatorGap";

    public const string BushingCount = "TR_BushingCount";
    public const string BushingDiameter = "TR_BushingDiameter";
    public const string BushingLength = "TR_BushingLength";
    public const string BushingShedCount = "TR_BushingShedCount";
    public const string TurretHeight = "TR_TurretHeight";

    public const string ConservatorDiameter = "TR_ConservatorDiameter";
    public const string ConservatorLength = "TR_ConservatorLength";
    public const string ConservatorLift = "TR_ConservatorLift";

    public const string GroupTank = "Tank, base and cover";
    public const string GroupRadiator = "Radiators";
    public const string GroupBushing = "Bushings";
    public const string GroupConservator = "Conservator";

    // Geometry constants shared between the validation rules and the builder (so the two never drift apart).
    public const double RadiatorClearanceFromTank = 30;
    public const double RadiatorBottomOffset = 150;
    public const double BushingEdgeMargin = 100;
    public const double BushingRowFraction = 0.22; // the bushing rows are placed at y = ±0.22 * tank width
    public const double TurretFactor = 0.6;        // turret radius = 0.6 * bushing diameter

    public static double TurretRadius(double bushingDiameter) => TurretFactor * bushingDiameter;

    /// <summary>Bushings per row: two rows when there are 2 or more bushings.</summary>
    public static int BushingRows(double count) => count >= 2 ? 2 : 1;

    public static int BushingsPerRow(double count) => (int)Math.Ceiling(count / BushingRows(count));

    public static readonly IReadOnlyList<ParameterDef> All = new[]
    {
        new ParameterDef(TankLength, "Tank length", GroupTank, 2000, 1000, 5000),
        new ParameterDef(TankWidth, "Tank width", GroupTank, 1100, 600, 2500),
        new ParameterDef(TankHeight, "Tank height", GroupTank, 1500, 800, 3500),
        new ParameterDef(BaseThickness, "Base plate thickness", GroupTank, 80, 40, 200),
        new ParameterDef(BaseOverhang, "Base plate overhang", GroupTank, 120, 40, 300),
        new ParameterDef(CoverThickness, "Cover thickness", GroupTank, 50, 20, 150),
        new ParameterDef(CoverOverhang, "Cover overhang", GroupTank, 40, 0, 120),

        new ParameterDef(RadiatorCount, "Radiator count", GroupRadiator, 3, 0, 6) { Unit = "", Integer = true },
        new ParameterDef(RadiatorWidth, "Radiator width", GroupRadiator, 400, 150, 800),
        new ParameterDef(RadiatorDepth, "Radiator depth", GroupRadiator, 220, 80, 500),
        new ParameterDef(RadiatorHeight, "Radiator height", GroupRadiator, 1000, 300, 2800),
        new ParameterDef(RadiatorGap, "Gap between radiators", GroupRadiator, 60, 20, 200),

        new ParameterDef(BushingCount, "Bushing count", GroupBushing, 6, 1, 8) { Unit = "", Integer = true },
        new ParameterDef(BushingDiameter, "Bushing diameter", GroupBushing, 200, 100, 400),
        new ParameterDef(BushingLength, "Bushing length", GroupBushing, 800, 300, 2000),
        new ParameterDef(BushingShedCount, "Shed count", GroupBushing, 8, 3, 14) { Unit = "", Integer = true },
        new ParameterDef(TurretHeight, "Turret height", GroupBushing, 200, 80, 500),

        new ParameterDef(ConservatorDiameter, "Conservator diameter", GroupConservator, 450, 200, 900),
        new ParameterDef(ConservatorLength, "Conservator length", GroupConservator, 1000, 400, 2500),
        new ParameterDef(ConservatorLift, "Conservator lift above cover", GroupConservator, 300, 150, 1000),
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

        double len = V(TankLength), wid = V(TankWidth), hgt = V(TankHeight);
        double radN = V(RadiatorCount), radW = V(RadiatorWidth), radH = V(RadiatorHeight), radG = V(RadiatorGap);
        double bushN = V(BushingCount), bushD = V(BushingDiameter);
        double consD = V(ConservatorDiameter), consL = V(ConservatorLength);

        if (radN > 0 && radN * radW + (radN - 1) * radG > len - 200 + eps)
            issues.Add(new ValidationIssue("The radiators do not fit along the tank length (count x width + gaps must be at most tank length - 200 mm).",
                new[] { RadiatorCount, RadiatorWidth, RadiatorGap, TankLength }));
        if (radN > 0 && radH > hgt - RadiatorBottomOffset - 100 + eps)
            issues.Add(new ValidationIssue($"Radiator height must be at most tank height - {RadiatorBottomOffset + 100:0} mm.", new[] { RadiatorHeight, TankHeight }));

        if (consL > wid - 100 + eps)
            issues.Add(new ValidationIssue("Conservator length must be at most tank width - 100 mm.", new[] { ConservatorLength, TankWidth }));
        if (consD > len / 3 + eps)
            issues.Add(new ValidationIssue("Conservator diameter must be at most one third of the tank length.", new[] { ConservatorDiameter, TankLength }));

        double rt = TurretRadius(bushD);
        int rows = BushingRows(bushN), perRow = BushingsPerRow(bushN);
        double availX = len - consD - 2 * rt - 2 * BushingEdgeMargin;
        if (availX < 0 || (perRow > 1 && availX / (perRow - 1) < 2 * rt + 20))
            issues.Add(new ValidationIssue("The bushings do not fit along the tank length (reduce count or diameter, or shorten the conservator).",
                new[] { BushingCount, BushingDiameter, ConservatorDiameter, TankLength }));
        if (rows == 2 && 2 * BushingRowFraction * wid < 2 * rt + 50 + eps)
            issues.Add(new ValidationIssue("The two bushing rows are too close for this diameter; widen the tank or use a smaller bushing diameter.",
                new[] { BushingDiameter, TankWidth }));
        if (rt > (rows == 2 ? (0.5 - BushingRowFraction) * wid : wid / 2 - 40) + eps)
            issues.Add(new ValidationIssue("The bushing turret does not fit on the tank cover; reduce the bushing diameter.", new[] { BushingDiameter, TankWidth }));

        return issues;
    }
}
