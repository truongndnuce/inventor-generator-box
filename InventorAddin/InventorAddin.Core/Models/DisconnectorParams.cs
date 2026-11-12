using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>
/// A high-voltage double-column disconnector (three phases in a row), as a simplified model: steel posts with base plates carrying a frame
/// (a longitudinal beam on each side and a cross beam under every phase), two ribbed insulator columns per phase, a horizontal blade arm joining the two
/// column tops of each phase, and a control cabinet hanging under the cross beam of the middle phase.
/// A single place of definition: the server (form, rule checks) and the add-in (built by Scripts\disconnector.gea) both read from here. Unit: mm.
/// Coordinate system: X is across the poles (the two columns of a phase are at +-ColumnSpacing/2), D is along the row of phases (centered at 0), the ground is at 0.
/// </summary>
public static class DisconnectorParams
{
    public const string Code = "disconnector";
    public const string Prefix = "DS_";

    public const string PhaseCount = "DS_PhaseCount";
    public const string PhaseSpacing = "DS_PhaseSpacing";
    public const string ColumnSpacing = "DS_ColumnSpacing";
    public const string ColumnDiameter = "DS_ColumnDiameter";
    public const string ColumnLength = "DS_ColumnLength";
    public const string ShedCount = "DS_ShedCount";

    public const string ArmHeight = "DS_ArmHeight";
    public const string ArmWidth = "DS_ArmWidth";

    public const string PostHeight = "DS_PostHeight";
    public const string PostSize = "DS_PostSize";
    public const string BeamHeight = "DS_BeamHeight";

    public const string CabWidth = "DS_CabWidth";
    public const string CabDepth = "DS_CabDepth";
    public const string CabHeight = "DS_CabHeight";

    public const string GroupPoles = "Poles and columns";
    public const string GroupArm = "Blade arm";
    public const string GroupFrame = "Support frame";
    public const string GroupCabinet = "Control cabinet";

    // Geometry constants shared between the validation rules and the builder (the script uses the same values).
    public const double TurretHeight = 120;     // the metal base ring of each column
    public const double TurretFactor = 1.3;     // its diameter = 1.3 x the column diameter
    public const double PlateFactor = 1.8;      // the base plate of a post = 1.8 x the post size
    public const double PlateHeight = 40;       // the base plate thickness
    public const double ArmOverhang = 200;      // the blade arm sticks out 200 mm beyond each column
    public const double Sink = 30;              // parts sink 30 mm into each other so the bodies join
    public const double CabClearance = 200;     // the cabinet stays at least 200 mm above the ground

    public static readonly IReadOnlyList<ParameterDef> All = new[]
    {
        new ParameterDef(PhaseCount, "Number of phases", GroupPoles, 3, 1, 3) { Unit = "", Integer = true },
        new ParameterDef(PhaseSpacing, "Phase spacing", GroupPoles, 2500, 1200, 5000),
        new ParameterDef(ColumnSpacing, "Spacing between the two columns", GroupPoles, 3000, 1200, 6000),
        new ParameterDef(ColumnDiameter, "Column diameter (largest shed)", GroupPoles, 400, 150, 700),
        new ParameterDef(ColumnLength, "Column length", GroupPoles, 2400, 800, 5000),
        new ParameterDef(ShedCount, "Sheds per column", GroupPoles, 16, 4, 40) { Unit = "", Integer = true },

        new ParameterDef(ArmHeight, "Blade arm height", GroupArm, 160, 60, 400),
        new ParameterDef(ArmWidth, "Blade arm width", GroupArm, 140, 60, 300),

        new ParameterDef(PostHeight, "Post height", GroupFrame, 2200, 800, 5000),
        new ParameterDef(PostSize, "Post size", GroupFrame, 300, 150, 600),
        new ParameterDef(BeamHeight, "Frame beam height", GroupFrame, 250, 100, 500),

        new ParameterDef(CabWidth, "Cabinet width", GroupCabinet, 500, 250, 1000),
        new ParameterDef(CabDepth, "Cabinet depth", GroupCabinet, 400, 200, 800),
        new ParameterDef(CabHeight, "Cabinet height", GroupCabinet, 600, 300, 1200),
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

    /// <summary>Elevation of the top of the frame beams (the columns stand on it).</summary>
    public static double FrameTop(IReadOnlyDictionary<string, double> v) => v[PostHeight] + v[BeamHeight];

    /// <summary>Elevation of the top of the insulator columns.</summary>
    public static double ColumnTop(IReadOnlyDictionary<string, double> v) => FrameTop(v) + TurretHeight + v[ColumnLength];

    /// <summary>Elevation of the highest point: the top of the blade arm.</summary>
    public static double TopHeight(IReadOnlyDictionary<string, double> v) => ColumnTop(v) + v[ArmHeight];

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

        double dia = V(ColumnDiameter);
        if (V(ColumnSpacing) < 3 * dia - eps)
            issues.Add(new ValidationIssue("Column spacing must be at least 3 x the column diameter.", new[] { ColumnSpacing, ColumnDiameter }));
        if (V(PhaseSpacing) < 3 * dia - eps)
            issues.Add(new ValidationIssue("Phase spacing must be at least 3 x the column diameter.", new[] { PhaseSpacing, ColumnDiameter }));
        if (TurretFactor * dia > PlateFactor * V(PostSize) + eps)
            issues.Add(new ValidationIssue("The column base ring (1.3 x the column diameter) must not be wider than 1.8 x the post size.", new[] { ColumnDiameter, PostSize }));
        if (V(PhaseSpacing) < PlateFactor * V(PostSize) + 100 - eps)
            issues.Add(new ValidationIssue("Phase spacing must be at least 1.8 x the post size + 100 mm (the base plates must not touch).", new[] { PhaseSpacing, PostSize }));
        if (V(CabHeight) > V(PostHeight) - CabClearance + eps)
            issues.Add(new ValidationIssue("Cabinet height must leave 200 mm of clearance under it: at most the post height - 200 mm.", new[] { CabHeight, PostHeight }));
        if (V(CabWidth) > V(ColumnSpacing) - V(PostSize) - 100 + eps)
            issues.Add(new ValidationIssue("Cabinet width must fit between the posts: at most the column spacing - post size - 100 mm.", new[] { CabWidth, ColumnSpacing, PostSize }));

        return issues;
    }
}
