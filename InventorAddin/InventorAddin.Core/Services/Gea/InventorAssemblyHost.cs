using System;
using System.Collections.Generic;
using System.Globalization;
using InventorAddin.Gea;
using Inv = Inventor;

namespace InventorAddin.Services.Gea;

/// <summary>
/// The Inventor Host for TARGET ASSEMBLY: <c>ADD_COMPONENT</c> inserts a part file at a position and rotation
/// (<c>Occurrences.Add</c>); names and attributes work on the occurrence. Builders (<c>CREATE_FEATURE</c>), <c>SET_PARAM_EXPR</c> and appearances
/// are Part-only and raise GEA4020. FORM parameters live in memory (an assembly has no User Parameters of its own here).
/// Used by the web worker with a newly created Assembly, so there is no ownership mark and no cleanup of an earlier build.
/// </summary>
internal sealed class InventorAssemblyHost : IGeaHost
{
    private const string AttrSet = "GEA_Attr";

    private readonly Inv.Application _app;
    private readonly Inv.AssemblyDocument _asm;
    private readonly Inv.AssemblyComponentDefinition _def;
    private readonly Func<string, string> _partPath;
    private readonly Action<string>? _progress;
    private readonly Dictionary<string, GeaValue> _params = new(StringComparer.Ordinal);

    /// <param name="partPath">Gives the full path of the .ipt for a part name (building it first if needed); throws <see cref="GeaRuntimeException"/> for an unknown name.</param>
    public InventorAssemblyHost(Inv.Application app, Inv.AssemblyDocument asm, Func<string, string> partPath, Action<string>? progress)
    {
        _app = app;
        _asm = asm;
        _def = asm.ComponentDefinition;
        _partPath = partPath;
        _progress = progress;
    }

    /// <summary>The part name of every ADD_COMPONENT, in order (the BOM counts them).</summary>
    public List<string> PlacedParts { get; } = new();

    // ================================================================ parameters

    public void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> values)
    {
        foreach (var (k, v) in values) _params[k] = v;
    }

    public void SetParam(string name, GeaValue value) => _params[name] = value;

    public void SetParamExpr(string name, string expression) =>
        throw new GeaRuntimeException("GEA4020", "SET_PARAM_EXPR is not supported in an assembly yet.");

    public GeaValue GetParam(string name, GeaType type)
    {
        if (!_params.TryGetValue(name, out var v))
            throw new GeaRuntimeException("GEA4099", $"Parameter '{name}' does not exist.");
        if (v.Type == type) return v;
        if (v.IsNumeric && type is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle) return GeaValue.Number(type, v.Num);
        throw new GeaRuntimeException("GEA2002", $"Parameter '{name}' is {GeaUnits.TypeName(v.Type)}, it cannot be read as {GeaUnits.TypeName(type)}.");
    }

    // ================================================================ components

    public IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs) =>
        throw new GeaRuntimeException("GEA4020", $"Builder {builder.Name} makes a body in a Part. In an assembly, build the part first and insert it with ADD_COMPONENT.");

    public GeaRef AddComponent(string part, double[] positionMm, double[] rotationDeg)
    {
        var path = _partPath(part);
        var occ = _def.Occurrences.Add(path, Transform(positionMm, rotationDeg));
        PlacedParts.Add(part.Trim());
        _progress?.Invoke($"Placed {part} at {positionMm[0]:0.##}, {positionMm[1]:0.##}, {positionMm[2]:0.##} mm.");
        var props = new Dictionary<string, GeaValue>(StringComparer.Ordinal) { ["Part"] = GeaValue.Text(part) };
        return new GeaRef("COMPONENT", occ, "component", props);
    }

    /// <summary>Rotation about X, then Y, then Z (degrees, about the assembly origin), then the translation (mm to Inventor's cm).</summary>
    private Inv.Matrix Transform(double[] positionMm, double[] rotationDeg)
    {
        var tg = _app.TransientGeometry;
        var m = tg.CreateMatrix();
        var origin = tg.CreatePoint(0, 0, 0);
        var axes = new[] { tg.CreateVector(1, 0, 0), tg.CreateVector(0, 1, 0), tg.CreateVector(0, 0, 1) };
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(rotationDeg[i]) < 1e-12) continue;
            var r = tg.CreateMatrix();
            r.SetToRotation(rotationDeg[i] * Math.PI / 180.0, axes[i], origin);
            m.PostMultiplyBy(r);
        }
        m.SetTranslation(tg.CreateVector(positionMm[0] / 10.0, positionMm[1] / 10.0, positionMm[2] / 10.0), true);
        return m;
    }

    private static Inv.ComponentOccurrence Occ(GeaRef r) =>
        r.Handle as Inv.ComponentOccurrence ?? throw new GeaRuntimeException("GEA4012", "The reference does not belong to the current Inventor assembly.");

    // ================================================================ names and attributes

    public void SetRefName(GeaRef target, string name)
    {
        var occ = Occ(target);
        string current = occ.Name;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Inv.ComponentOccurrence o in _def.Occurrences) names.Add(o.Name);
        occ.Name = GeaNames.Resolve(name, n => !string.Equals(n, current, StringComparison.OrdinalIgnoreCase) && names.Contains(n));
    }

    public string GetRefName(GeaRef target) => Occ(target).Name;

    public void SetRefAttr(GeaRef target, string key, GeaValue value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new GeaRuntimeException("GEA4099", "The attribute key must not be empty.");
        var sets = Occ(target).AttributeSets;
        var set = sets.NameIsUsed[AttrSet] ? sets[AttrSet] : sets.Add(AttrSet);
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
        var sets = Occ(target).AttributeSets;
        if (!sets.NameIsUsed[AttrSet]) return null;
        var set = sets[AttrSet];
        if (!set.NameIsUsed[key]) return null;
        var a = set[key];
        return a.ValueType switch
        {
            Inv.ValueTypeEnum.kIntegerType => GeaValue.Int(Convert.ToInt64(a.Value, CultureInfo.InvariantCulture)),
            Inv.ValueTypeEnum.kDoubleType => GeaValue.Dbl(Convert.ToDouble(a.Value, CultureInfo.InvariantCulture)),
            _ => a.Value is string s && (s == "TRUE" || s == "FALSE") ? GeaValue.Boolean(s == "TRUE") : GeaValue.Text(Convert.ToString(a.Value, CultureInfo.InvariantCulture) ?? ""),
        };
    }

    public void SetAppearance(GeaRef target, string appearance) =>
        throw new GeaRuntimeException("GEA4020", "SET_APPEARANCE is not supported on an assembly component yet; set the appearance in the part.");

    // ================================================================ other

    public void Regen() => _asm.Update();

    public void Progress(GeaProgressKind kind, string? text, double? percent)
    {
        if (!string.IsNullOrEmpty(text)) _progress?.Invoke(text);
    }

    public void Print(string message, int line) => _progress?.Invoke(message);
}
