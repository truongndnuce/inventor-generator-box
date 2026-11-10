using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InventorAddin.Gea;
using Inv = Inventor;

namespace InventorAddin.Services.Gea;

/// <summary>Ownership mark: every object a .gea script creates in the Part carries the AttributeSet "GEA_Owner" with the binding id of the Part.</summary>
internal static class GeaOwnership
{
    public const string OwnerSet = "GEA_Owner";
    public const string BindingKey = "Binding";
    public const string AttrSet = "GEA_Attr";

    public static Inv.AttributeSets? SetsOf(object o) => o switch
    {
        Inv.PartFeature f => f.AttributeSets,
        Inv.PlanarSketch s => s.AttributeSets,
        Inv.WorkPlane w => w.AttributeSets,
        Inv.UserParameter p => p.AttributeSets,
        Inv.SurfaceBody b => b.AttributeSets,
        _ => null,
    };

    public static void Own(object o, string binding)
    {
        var sets = SetsOf(o);
        if (sets == null) return;
        var set = sets.NameIsUsed[OwnerSet] ? sets[OwnerSet] : sets.Add(OwnerSet);
        if (set.NameIsUsed[BindingKey]) set[BindingKey].Value = binding;
        else set.Add(BindingKey, Inv.ValueTypeEnum.kStringType, binding);
    }

    public static bool IsOwned(object o, string binding) => OwnerOf(o) == binding;

    public static string? OwnerOf(object o)
    {
        try
        {
            var sets = SetsOf(o);
            if (sets == null || !sets.NameIsUsed[OwnerSet]) return null;
            var set = sets[OwnerSet];
            return set.NameIsUsed[BindingKey] ? set[BindingKey].Value as string : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The Inventor Host of .gea: synchronizes User Parameters, runs the C# builders with the Inventor API, sets names, attaches attributes and appearances.
/// Runs inside the Transaction opened by <see cref="GeaDocumentService"/>; every object it creates gets the ownership mark.
/// </summary>
internal sealed class InventorGeaHost : IGeaHost
{
    private const double Eps = 1e-6;

    private readonly Inv.Application _app;
    private readonly Inv.PartDocument _part;
    private readonly Inv.PartComponentDefinition _def;
    private readonly string _binding;
    private readonly PartGeometry _geo;
    private readonly Action<string>? _progress;
    private readonly HashSet<string> _touchedParams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Inv.Asset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _builderCount = new(StringComparer.Ordinal);
    private string? _progressTitle;

    public InventorGeaHost(Inv.Application app, Inv.PartDocument part, string binding, Action<string>? progress)
    {
        _app = app;
        _part = part;
        _def = part.ComponentDefinition;
        _binding = binding;
        _progress = progress;
        _geo = new PartGeometry(app, part, o => GeaOwnership.Own(o, _binding));
    }

    public List<string> Warnings { get; } = new();

    // ================================================================ parameters

    public void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> values)
    {
        foreach (var (name, value) in values) WriteParam(name, value);
    }

    public void SetParam(string name, GeaValue value) => WriteParam(name, value);

    public void SetParamExpr(string name, string expression)
    {
        var p = OwnedOrNewParam(name);
        if (p != null)
        {
            p.Expression = expression;
            return;
        }
        Exception? last = null;
        foreach (var units in new[] { "mm", "ul", "deg" })
        {
            try
            {
                p = _def.Parameters.UserParameters.AddByExpression(name, expression, units);
                GeaOwnership.Own(p, _binding);
                return;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new GeaRuntimeException("GEA4099", $"Could not create Parameter {name} = \"{expression}\": {last?.Message}");
    }

    public GeaValue GetParam(string name, GeaType type)
    {
        Inv.Parameter p;
        try { p = _def.Parameters[name]; }
        catch { throw new GeaRuntimeException("GEA4099", $"Parameter '{name}' does not exist in the Part."); }

        object value = p.Value;
        switch (type)
        {
            case GeaType.Length: return GeaValue.Length(Convert.ToDouble(value, CultureInfo.InvariantCulture) * 10.0);
            case GeaType.Angle: return GeaValue.Angle(Convert.ToDouble(value, CultureInfo.InvariantCulture) * 180.0 / Math.PI);
            case GeaType.Integer: return GeaValue.Int((long)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
            case GeaType.Double: return GeaValue.Dbl(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case GeaType.Bool:
                if (value is bool b) return GeaValue.Boolean(b);
                break;
            case GeaType.String:
                return GeaValue.Text(value is string s ? s : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        }
        throw new GeaRuntimeException("GEA2002", $"Parameter '{name}' cannot be read as {GeaUnits.TypeName(type)}.");
    }

    /// <summary>Creates or updates a User Parameter according to the type of the value. A Parameter with the same name that this script did not create: GEA4010.</summary>
    private void WriteParam(string name, GeaValue value)
    {
        var p = OwnedOrNewParam(name);
        var (internalValue, unitsEnum, unitsText) = ToInventor(value);

        if (p != null)
        {
            try
            {
                if (unitsText != null && !string.Equals(Convert.ToString(p.get_Units(), CultureInfo.InvariantCulture), unitsText, StringComparison.OrdinalIgnoreCase))
                    p.set_Units(unitsText);
                if (!Equals(p.Value, internalValue)) p.Value = internalValue;
                return;
            }
            catch
            {
                // The parameter type changed (for example from LENGTH to STRING): delete it and create it again
                try { p.Delete(); }
                catch (Exception ex)
                {
                    throw new GeaRuntimeException("GEA4099", $"Parameter {name} changed type but could not be deleted to recreate it: {ex.Message}");
                }
            }
        }

        try
        {
            p = _def.Parameters.UserParameters.AddByValue(name, internalValue, unitsEnum);
        }
        catch (Exception ex)
        {
            if (value.Type is GeaType.Bool or GeaType.String)
            {
                // Some versions have no text/boolean Parameter: do not block the build
                Warnings.Add($"Could not create Parameter {name} ({GeaUnits.TypeName(value.Type)}): {ex.Message}");
                return;
            }
            throw new GeaRuntimeException("GEA4099", $"Could not create Parameter {name}: {ex.Message}");
        }
        GeaOwnership.Own(p, _binding);
    }

    /// <summary>The Parameter of this script (if any). Null if there is none. A name clash with another Parameter: GEA4010.</summary>
    private Inv.UserParameter? OwnedOrNewParam(string name)
    {
        _touchedParams.Add(name);
        Inv.UserParameter? found = null;
        foreach (Inv.UserParameter p in _def.Parameters.UserParameters)
        {
            if (p.Name == name) { found = p; break; }
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new GeaRuntimeException("GEA4010", $"A Parameter '{p.Name}' already exists that differs from '{name}' only in letter case.");
        }
        if (found == null)
        {
            // Clashes with a model Parameter (d0, d1...) or a reference
            try
            {
                var any = _def.Parameters[name];
                if (any != null) throw new GeaRuntimeException("GEA4010", $"The name '{name}' is already used by another Parameter in the Part.");
            }
            catch (GeaRuntimeException) { throw; }
            catch { /* not found: it may be created */ }
            return null;
        }
        if (!GeaOwnership.IsOwned(found, _binding))
            throw new GeaRuntimeException("GEA4010",
                $"Parameter '{name}' already exists in the Part but was not created by this script. It is not overwritten; rename the parameter or delete that Parameter.");
        return found;
    }

    private static (object value, object unitsEnum, string? unitsText) ToInventor(GeaValue v) => v.Type switch
    {
        GeaType.Length => (v.Num / 10.0, Inv.UnitsTypeEnum.kMillimeterLengthUnits, "mm"),
        GeaType.Angle => (v.Num * Math.PI / 180.0, Inv.UnitsTypeEnum.kDegreeAngleUnits, "deg"),
        GeaType.Integer => (Math.Round(v.Num), Inv.UnitsTypeEnum.kUnitlessUnits, "ul"),
        GeaType.Double => (v.Num, Inv.UnitsTypeEnum.kUnitlessUnits, "ul"),
        GeaType.Bool => (v.Bool, Inv.UnitsTypeEnum.kBooleanUnits, null),
        GeaType.String => (v.Str ?? "", Inv.UnitsTypeEnum.kTextUnits, null),
        _ => throw new GeaRuntimeException("GEA2002", "A reference is not stored as a Parameter."),
    };

    /// <summary>Deletes the Parameters this script created last time that are no longer declared this time.</summary>
    public void RemoveStaleParameters()
    {
        var stale = new List<Inv.UserParameter>();
        foreach (Inv.UserParameter p in _def.Parameters.UserParameters)
            if (!_touchedParams.Contains(p.Name) && GeaOwnership.IsOwned(p, _binding))
                stale.Add(p);
        foreach (var p in stale)
        {
            var name = p.Name;
            try { p.Delete(); }
            catch (Exception ex) { Warnings.Add($"Parameter {name} is no longer in the script but could not be deleted (in use?): {ex.Message}"); }
        }
    }

    // ================================================================ builder

    public IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs)
    {
        double L(string n) => inputs[n].Num;
        int n = _builderCount[builder.Name] = _builderCount.TryGetValue(builder.Name, out var c) ? c + 1 : 1;
        string tag = $"GEA_{builder.Name}{n}";

        PartGeometry.Built built;
        var extra = new Dictionary<string, PartGeometry.Built>(StringComparer.Ordinal);
        var props = new Dictionary<string, GeaValue>(inputs, StringComparer.Ordinal);
        switch (builder.Name)
        {
            case "Tank":
            {
                double len = L("Length"), wid = L("Width"), hgt = L("Height"), wall = L("Wall"), bottom = L("Bottom");
                double cr = L("CornerRadius"), ir = L("InnerRadius"), skirt = L("SkirtHeight");
                Require(len > 0 && wid > 0 && hgt > 0, "Tank: Length, Width, Height must be positive.");
                Require(wall > 0 && 2 * wall < Math.Min(len, wid), "Tank: Wall must be positive and smaller than half of the shorter side.");
                Require(bottom > 0 && bottom < hgt, "Tank: Bottom must be positive and smaller than Height.");
                Require(cr >= 0 && cr <= Math.Min(len, wid) / 2, "Tank: CornerRadius must not exceed half of the shorter side.");
                Require(skirt >= 0, "Tank: SkirtHeight must not be negative.");
                double inSx = len - 2 * wall, inSd = wid - 2 * wall;
                ir = Math.Min(Math.Max(ir, 0), Math.Min(inSx, inSd) / 2);

                built = _geo.Prism(tag, 0, 0, skirt, len, wid, cr, hgt);
                _geo.Pocket(built, tag + "_Cavity", 0, 0, skirt + hgt, inSx, inSd, ir, hgt - bottom);
                if (skirt > Eps)
                    _geo.JoinPrism(built, tag + "_Skirt", 0, 0, 0, len, wid, cr, skirt, up: true, inSx, inSd, ir);
                props["Top"] = GeaValue.Length(skirt + hgt);
                props["InnerRadius"] = GeaValue.Length(ir);
                break;
            }
            case "Cover":
            {
                var tank = inputs["Tank"].Ref!;
                if (tank.Builder != "Tank")
                    throw new GeaRuntimeException("GEA4099", "Cover: the Tank input must be a tank created by builder:Tank.");
                double TP(string k) => tank.Props.TryGetValue(k, out var v) ? v.Num : 0;
                double len = TP("Length"), wid = TP("Width"), wall = TP("Wall"), cr = TP("CornerRadius"), ir = TP("InnerRadius");
                double top = TP("Top");
                double thick = L("Thickness"), lip = L("Lip"), clr = L("Clearance"), gap = L("Gap");
                Require(thick > 0, "Cover: Thickness must be positive.");
                Require(lip >= 0 && clr >= 0 && gap >= 0, "Cover: Lip, Clearance, Gap must not be negative.");
                double lipSx = len - 2 * wall - 2 * clr, lipSd = wid - 2 * wall - 2 * clr;
                Require(lip <= Eps || (lipSx > 0 && lipSd > 0), "Cover: the clearance is too large, there is no room left for the lid lip.");

                double h0 = top + gap;
                built = _geo.Prism(tag, 0, 0, h0, len, wid, cr, thick);
                if (lip > Eps)
                    _geo.JoinPrism(built, tag + "_Lip", 0, 0, h0, lipSx, lipSd, Math.Max(ir - clr, 0), lip, up: false);
                props["Top"] = GeaValue.Length(h0 + thick);
                break;
            }
            case "Block":
            {
                double sx = L("SizeX"), sd = L("SizeD"), h = L("Height");
                Require(sx > 0 && sd > 0 && h > 0, "Block: SizeX, SizeD, Height must be positive.");
                built = _geo.Prism(tag, L("CenterX"), L("CenterD"), L("Elevation"), sx, sd, L("CornerRadius"), h);
                break;
            }
            case "Cylinder":
            {
                double dia = L("Diameter"), len = L("Length");
                Require(dia > 0 && len > 0, "Cylinder: Diameter, Length must be positive.");
                var axis = (inputs["Axis"].Str ?? "D").Trim().ToUpperInvariant();
                try { built = _geo.Cylinder(tag, axis, L("CenterX"), L("CenterD"), L("Elevation"), dia, len); }
                catch (ArgumentException ex) { throw new GeaRuntimeException("GEA4099", "Cylinder: " + ex.Message); }
                break;
            }
            case "Bushing":
            {
                double dia = L("Diameter"), len = L("Length"), turret = L("TurretHeight");
                int sheds = (int)Math.Round(inputs["ShedCount"].Num);
                Require(dia > 0 && len > 0 && turret > 0, "Bushing: Diameter, Length, TurretHeight must be positive.");
                Require(sheds >= 1 && sheds <= 100, "Bushing: ShedCount must be from 1 to 100.");
                Require(L("TurretDiameter") >= 0 && L("TurretTaper") >= 0 && L("CapHeight") >= 0,
                    "Bushing: TurretDiameter, TurretTaper, CapHeight must not be negative.");
                try
                {
                    bool separate = inputs["SeparateTurret"].Bool;
                    var (porcelain, turretBody) = _geo.Bushing(tag, L("CenterX"), L("CenterD"), L("Elevation"), dia, len, turret, sheds,
                        L("TurretDiameter"), L("TurretTaper"), L("CapHeight"), separate);
                    built = porcelain;
                    if (separate)
                    {
                        try { turretBody.Body.Name = tag + "_Turret"; } catch { /* keep the default name */ }
                        extra["Turret"] = turretBody;
                    }
                }
                catch (ArgumentException ex) { throw new GeaRuntimeException("GEA4099", "Bushing: " + ex.Message); }
                break;
            }
            case "Radiator":
            {
                double sx = L("SizeX"), sd = L("SizeD"), h = L("Height");
                double fin = L("FinThickness"), header = L("HeaderHeight"), end = L("EndPlate");
                int fins = (int)Math.Round(inputs["FinCount"].Num);
                Require(sx > 0 && sd > 0 && h > 0, "Radiator: SizeX, SizeD, Height must be positive.");
                Require(fins >= 1 && fins <= 500, "Radiator: FinCount must be from 1 to 500.");
                Require(fin > 0 && header > 0 && end > 0, "Radiator: FinThickness, HeaderHeight, EndPlate must be positive.");
                Require(2 * header < h, "Radiator: the two headers are taller than the whole radiator.");
                double inner = sx - 2 * end;
                Require(inner > fins * fin * 1.2, $"Radiator: {fins} fins of thickness {fin} mm do not fit in the inner width of {inner:0.#} mm (a gap between fins is needed).");
                built = _geo.Radiator(tag, L("CenterX"), L("CenterD"), L("Elevation"), sx, sd, h, fins, fin, header, end);
                break;
            }
            default:
                throw new GeaRuntimeException("GEA2013", $"The Inventor Host has no builder '{builder.Name}' yet.");
        }

        try { built.Body.Name = tag; } catch { /* keep the default name */ }
        var r = new GeaRef("BODY", built, builder.Name, props);
        // Secondary outputs (for example a separate turret) are other bodies; an output without its own body points to the main body
        return builder.Outputs.ToDictionary(o => o.Name,
            o => extra.TryGetValue(o.Name, out var b) ? new GeaRef("BODY", b, builder.Name, props) : r);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new GeaRuntimeException("GEA4099", message);
    }

    private static PartGeometry.Built Built(GeaRef r) =>
        r.Handle as PartGeometry.Built ?? throw new GeaRuntimeException("GEA4012", "The reference does not belong to the current Inventor model.");

    // ================================================================ names and attributes

    public void SetRefName(GeaRef target, string name)
    {
        var built = Built(target);
        var body = built.Body;
        string current = body.Name;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Inv.SurfaceBody b in _def.SurfaceBodies) names.Add(b.Name);

        var resolved = GeaNames.Resolve(name, n => !string.Equals(n, current, StringComparison.OrdinalIgnoreCase) && names.Contains(n));
        body.Name = resolved;

        // Rename the accompanying sketches and features so they are easy to read in the browser tree
        foreach (var (item, role) in built.Items)
        {
            var newName = $"{resolved}_{role}";
            try
            {
                switch (item)
                {
                    case Inv.PlanarSketch s: s.Name = newName; break;
                    case Inv.PartFeature f: f.Name = newName; break;
                }
            }
            catch { /* duplicate name: keep the old name */ }
        }
    }

    public string GetRefName(GeaRef target) => Built(target).Body.Name;

    public void SetRefAttr(GeaRef target, string key, GeaValue value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new GeaRuntimeException("GEA4099", "The attribute key must not be empty.");
        var sets = Built(target).Body.AttributeSets;
        var set = sets.NameIsUsed[GeaOwnership.AttrSet] ? sets[GeaOwnership.AttrSet] : sets.Add(GeaOwnership.AttrSet);
        var (type, v) = value.Type switch
        {
            GeaType.Integer => (Inv.ValueTypeEnum.kIntegerType, (object)(int)Math.Round(value.Num)),
            GeaType.Double or GeaType.Length or GeaType.Angle => (Inv.ValueTypeEnum.kDoubleType, value.Num),
            GeaType.Bool => (Inv.ValueTypeEnum.kStringType, value.Bool ? "TRUE" : "FALSE"),
            _ => (Inv.ValueTypeEnum.kStringType, (object)(value.Str ?? "")),
        };
        if (set.NameIsUsed[key])
        {
            var a = set[key];
            if (a.ValueType == type)
            {
                a.Value = v;
                return;
            }
            a.Delete();
        }
        set.Add(key, type, v);
    }

    public GeaValue? GetRefAttr(GeaRef target, string key)
    {
        var sets = Built(target).Body.AttributeSets;
        if (!sets.NameIsUsed[GeaOwnership.AttrSet]) return null;
        var set = sets[GeaOwnership.AttrSet];
        if (!set.NameIsUsed[key]) return null;
        var a = set[key];
        return a.ValueType switch
        {
            Inv.ValueTypeEnum.kIntegerType => GeaValue.Int(Convert.ToInt64(a.Value, CultureInfo.InvariantCulture)),
            Inv.ValueTypeEnum.kDoubleType => GeaValue.Dbl(Convert.ToDouble(a.Value, CultureInfo.InvariantCulture)),
            _ => a.Value is string s && (s == "TRUE" || s == "FALSE") ? GeaValue.Boolean(s == "TRUE") : GeaValue.Text(Convert.ToString(a.Value, CultureInfo.InvariantCulture) ?? ""),
        };
    }

    // ================================================================ appearances

    /// <summary>Familiar name → appearance id in the Inventor library (tried before creating a color of our own).</summary>
    private static readonly Dictionary<string, string[]> LibraryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grey"] = new[] { "Plastic-010" },       // "Smooth - Light Gray"
        ["Gray"] = new[] { "Plastic-010" },
        ["Porcelain"] = new[] { "Fabric-006" },   // "Dark Brown": dark brown like porcelain
    };

    /// <summary>Fallback colors (RGB) when the library has none: creates a "Generic" appearance inside the Part.</summary>
    private static readonly Dictionary<string, (byte r, byte g, byte b)> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grey"] = (160, 164, 170), ["Gray"] = (160, 164, 170), ["LightGrey"] = (201, 205, 209), ["LightGray"] = (201, 205, 209), ["Green"] = (64, 128, 80), ["Red"] = (190, 40, 40),
        ["Blue"] = (50, 90, 170), ["Brown"] = (100, 60, 35), ["Porcelain"] = (90, 50, 30), ["White"] = (235, 235, 235),
        ["Black"] = (30, 30, 30), ["Yellow"] = (230, 190, 40), ["Orange"] = (230, 120, 30), ["Silver"] = (192, 192, 192),
    };

    public void SetAppearance(GeaRef target, string appearance)
    {
        var asset = ResolveAppearance(appearance.Trim());
        Built(target).Body.Appearance = asset;
    }

    private Inv.Asset ResolveAppearance(string name)
    {
        if (_assets.TryGetValue(name, out var cached)) return cached;
        var doc = (Inv._Document)_part;
        Inv.Asset? found = null;
        bool hasAlias = LibraryAliases.TryGetValue(name, out var alias);
        bool isColor = TryColor(name, out var rgb);
        string colorId = "GEA_" + Sanitize(name);

        Inv.Asset? InPart(Func<Inv.Asset, bool> match)
        {
            try
            {
                foreach (Inv.Asset a in _part.AppearanceAssets)
                    if (match(a)) return a;
            }
            catch { /* ignore */ }
            return null;
        }

        if (!hasAlias && isColor)
        {
            // A named color or #RRGGBB with no alias: always use the opaque color created by the add-in. Do not look it up by name in the library,
            // because the library has appearances with the same name that are completely different (for example "Green" is transparent glass).
            // Inventor ignores the name passed to Assets.Add (it names it "1:<DisplayName>"), so a color created by the add-in is recognized by
            // its display name and an empty category (an asset copied from the library always has a category).
            found = InPart(a => (string.Equals(a.DisplayName, name, StringComparison.OrdinalIgnoreCase) && IsOwnColor(a))
                                || string.Equals(a.DisplayName, name + " (GEA)", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // 1. Already in the Part (by display name or id)
            found = InPart(a => string.Equals(a.DisplayName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // 2. The Inventor library: by alias or by id
        if (found == null && (hasAlias || !isColor))
        {
            var ids = hasAlias ? alias!.Append(name) : new[] { name };
            foreach (var id in ids)
            {
                foreach (Inv.AssetLibrary lib in _app.AssetLibraries)
                {
                    Inv.Asset? a = null;
                    try { a = lib.AppearanceAssets[id]; } catch { /* this library does not have it */ }
                    if (a == null) continue;
                    try { found = a.CopyTo(doc, true); } catch { found = null; }
                    if (found != null) break;
                }
                if (found != null) break;
            }
        }

        // 3. A named color or #RRGGBB: create a Generic appearance in the Part
        if (found == null && isColor)
        {
            try
            {
                Inv.Asset asset;
                try { asset = _part.Assets.Add(Inv.AssetTypeEnum.kAssetTypeAppearance, "Generic", colorId, name); }
                catch
                {
                    // The display name is already taken by another appearance (for example copied from the library): use a name of our own
                    asset = _part.Assets.Add(Inv.AssetTypeEnum.kAssetTypeAppearance, "Generic", colorId, name + " (GEA)");
                }
                var diffuse = (Inv.ColorAssetValue)asset["generic_diffuse"];
                diffuse.Value = _app.TransientObjects.CreateColor(rgb.r, rgb.g, rgb.b);
                found = asset;
            }
            catch (Exception ex)
            {
                Warnings.Add($"Could not create appearance '{name}': {ex.Message}");
            }
        }

        if (found == null)
            throw new GeaRuntimeException("GEA4020",
                $"There is no appearance '{name}'. Use a name from the Inventor library, a #RRGGBB code, or one of the colors: {string.Join(", ", Colors.Keys)}.");
        _assets[name] = found;
        return found;
    }

    private static bool IsOwnColor(Inv.Asset a)
    {
        try
        {
            return string.IsNullOrEmpty(a.CategoryName)
                && (a.Name.StartsWith("GEA_", StringComparison.OrdinalIgnoreCase) || ((Inv.FloatAssetValue)a["generic_transparency"]).Value < 0.01);
        }
        catch { return false; }
    }

    private static bool TryColor(string name, out (byte r, byte g, byte b) rgb)
    {
        if (Colors.TryGetValue(name, out rgb)) return true;
        if (name.Length == 7 && name[0] == '#' && int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            rgb = ((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
            return true;
        }
        return false;
    }

    private static string Sanitize(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    // ================================================================ other

    public void Regen() => _part.Update();

    public void Progress(GeaProgressKind kind, string? text, double? percent)
    {
        switch (kind)
        {
            case GeaProgressKind.Start:
                _progressTitle = text;
                _progress?.Invoke(text ?? "");
                break;
            case GeaProgressKind.State:
                var pct = percent.HasValue ? $" ({percent:0}%)" : "";
                _progress?.Invoke(_progressTitle != null ? $"{_progressTitle}: {text}{pct}" : $"{text}{pct}");
                break;
            case GeaProgressKind.End:
                _progressTitle = null;
                break;
        }
    }

    public void Print(string message, int line) => _progress?.Invoke(message);
}
