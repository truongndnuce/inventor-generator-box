using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Gea;

/// <summary>One input or output of a builder (section 12 of the spec).</summary>
public sealed record BuilderPort(string Name, GeaType Type, bool Required = true, GeaValue? Default = null, string Description = "");

/// <summary>
/// Manifest of a builder: the contract between the script and the C# build function. The semantic check uses the manifest to check
/// CREATE_FEATURE without Inventor; the Inventor Host runs the real build function of the same name.
/// </summary>
public sealed record BuilderManifest(
    string Name, string Version, string Target, string Description,
    IReadOnlyList<BuilderPort> Inputs, IReadOnlyList<BuilderPort> Outputs)
{
    public BuilderPort? Input(string name) => Inputs.FirstOrDefault(p => p.Name == name);

    public BuilderPort? Output(string name) => Outputs.FirstOrDefault(p => p.Name == name);
}

/// <summary>
/// Catalog of the built-in builders. Common coordinate system (as in the transformer model): X along the length, "D" along the depth (the Z axis of Inventor),
/// height along the Y axis of Inventor ("Elevation" is the elevation of the bottom, the ground is 0). All sizes are in mm.
/// </summary>
public static class GeaBuilderCatalog
{
    private static readonly GeaValue Zero = GeaValue.Length(0);

    private static BuilderPort L(string name, string description, bool required = true) =>
        new(name, GeaType.Length, required, required ? null : Zero, description);

    private static readonly BuilderPort BodyOut = new("Body", GeaType.Reference, Description: "The solid body just created");

    public static readonly IReadOnlyList<BuilderManifest> All = new[]
    {
        new BuilderManifest("Tank", "1.0", "PART",
            "Open-top hollow tank: a box with vertical rounded corners, wall thickness Wall, bottom thickness Bottom, and a skirt below. The tank center is at the origin.",
            new[]
            {
                L("Length", "Outer length (X)"), L("Width", "Outer width (D)"), L("Height", "Body height (without the skirt)"),
                L("Wall", "Wall thickness"), L("Bottom", "Bottom thickness"),
                L("CornerRadius", "Outer corner radius", false), L("InnerRadius", "Inner corner radius", false),
                L("SkirtHeight", "Skirt height", false),
            },
            new[] { BodyOut }),

        new BuilderManifest("Cover", "1.0", "PART",
            "Flat lid placed on the tank opening, with a lip that fits into the tank (separated from the inner wall by a clearance).",
            new[]
            {
                new BuilderPort("Tank", GeaType.Reference, true, null, "The tank created by builder:Tank"),
                L("Thickness", "Lid thickness"), L("Lip", "Lip height"), L("Clearance", "Clearance between the lip and the inner wall", false),
                L("Gap", "Distance the lid is lifted above the tank opening", false),
            },
            new[] { BodyOut }),

        new BuilderManifest("Block", "1.0", "PART",
            "A rectangular box (vertical corners can be rounded).",
            new[]
            {
                L("CenterX", "Center along X", false), L("CenterD", "Center along the depth", false), L("Elevation", "Elevation of the bottom face", false),
                L("SizeX", "Size along X"), L("SizeD", "Size along the depth"), L("Height", "Height"),
                L("CornerRadius", "Radius of the vertical corners", false),
            },
            new[] { BodyOut }),

        new BuilderManifest("Cylinder", "1.0", "PART",
            "A cylinder. Axis \"D\" (default): horizontal along the depth, symmetric about CenterD; \"X\": horizontal along X, symmetric about CenterX; " +
            "\"H\": vertical, bottom at Elevation. For a horizontal cylinder, Elevation is the elevation of the axis.",
            new[]
            {
                L("CenterX", "Center along X", false), L("CenterD", "Center along the depth", false), L("Elevation", "Elevation of the axis (horizontal) or of the bottom (vertical)", false),
                L("Diameter", "Diameter"), L("Length", "Cylinder length"),
                new BuilderPort("Axis", GeaType.String, false, GeaValue.Text("D"), "D, X or H"),
            },
            new[] { BodyOut }),

        new BuilderManifest("Bushing", "1.0", "PART",
            "Vertical bushing: a cylindrical turret (base) and an insulator body whose sheds shrink going up. One solid of revolution.",
            new[]
            {
                L("CenterX", "Center along X", false), L("CenterD", "Center along the depth", false), L("Elevation", "Elevation of the turret bottom", false),
                L("Diameter", "Diameter of the largest shed"), L("Length", "Length of the insulator body (above the turret)"),
                L("TurretHeight", "Turret height"),
                new BuilderPort("ShedCount", GeaType.Integer, true, null, "Number of sheds"),
                L("TurretDiameter", "Turret diameter (0: 1.2 x Diameter)", false),
                L("TurretTaper", "Height of the tapered part at the top of the turret (0: flat top)", false),
                L("CapHeight", "Height of the terminal cap at the top of the bushing (0: none)", false),
                new BuilderPort("SeparateTurret", GeaType.Bool, false, GeaValue.Boolean(false),
                    "TRUE: the turret is a separate body (output Turret), so it can have a different color from the insulator body"),
            },
            new[] { BodyOut, new BuilderPort("Turret", GeaType.Reference, Description: "The turret (the same body as Body if not separated)") }),

        new BuilderManifest("Radiator", "1.0", "PART",
            "Plate-type radiator: upper and lower headers, vertical parallel fins along the depth, and two end plates. One solid body.",
            new[]
            {
                L("CenterX", "Center along X", false), L("CenterD", "Center along the depth", false), L("Elevation", "Elevation of the bottom face", false),
                L("SizeX", "Width along X"), L("SizeD", "Depth (perpendicular to the tank wall)"), L("Height", "Height"),
                new BuilderPort("FinCount", GeaType.Integer, true, null, "Number of fins"),
                new BuilderPort("FinThickness", GeaType.Length, false, GeaValue.Length(12), "Fin thickness"),
                new BuilderPort("HeaderHeight", GeaType.Length, false, GeaValue.Length(80), "Height of the upper/lower header"),
                new BuilderPort("EndPlate", GeaType.Length, false, GeaValue.Length(25), "Thickness of the end plates"),
            },
            new[] { BodyOut }),
    };

    public static BuilderManifest? Find(string name) => All.FirstOrDefault(m => m.Name == name);

    /// <summary>Complete inputs (adds the defaults for optional inputs that were left out).</summary>
    public static Dictionary<string, GeaValue> CompleteInputs(BuilderManifest m, IReadOnlyDictionary<string, GeaValue> given)
    {
        var result = new Dictionary<string, GeaValue>(given, StringComparer.Ordinal);
        foreach (var p in m.Inputs)
            if (!result.ContainsKey(p.Name) && p.Default != null)
                result[p.Name] = p.Default;
        return result;
    }
}
