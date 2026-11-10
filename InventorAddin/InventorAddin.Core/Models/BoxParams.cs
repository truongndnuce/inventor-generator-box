using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Models;

/// <summary>Definition of one parameter (a single place; the UI, presets and adapters all read from here). Unit: mm.</summary>
public sealed record ParameterDef(string Name, string Label, string Group, double Default, double Min, double Max)
{
    /// <summary>Display unit ("mm", "" for counts).</summary>
    public string Unit { get; init; } = "mm";

    /// <summary>Integer (number of bushings, radiator fins...): the form uses step 1 and a rule that rejects fractional values.</summary>
    public bool Integer { get; init; }
}

/// <summary>One constraint error, with the list of related parameters so the UI can turn them red.</summary>
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

    /// <summary>Takes the values present in <paramref name="partial"/>, uses the default for missing parameters; unknown keys are dropped.</summary>
    public static Dictionary<string, double> Complete(IReadOnlyDictionary<string, double>? partial)
    {
        var result = Defaults();
        if (partial == null) return result;
        foreach (var d in All)
            if (partial.TryGetValue(d.Name, out var v))
                result[d.Name] = v;
        return result;
    }

    /// <summary>Checks min/max and the geometric constraints (section 4 of the plan). Empty means valid.</summary>
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
