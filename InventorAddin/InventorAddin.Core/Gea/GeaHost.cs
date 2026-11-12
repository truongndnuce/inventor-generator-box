using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Gea;

public enum GeaProgressKind { Start, State, End }

/// <summary>
/// The Host interface (section 14 of the mapping, section 14 of the spec). The interpreter calls the Host for everything that touches the model.
/// Two implementations: the Inventor Host (add-in, worker) and <see cref="GeaSimulationHost"/> (dry run, tests).
/// A Host error throws <see cref="GeaRuntimeException"/> with code GEA4xxx; other exceptions are wrapped as GEA4099.
/// </summary>
public interface IGeaHost
{
    /// <summary>Synchronizes every FORM parameter as a User Parameter before the first line of BUILD (section 9.1).</summary>
    void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> values);

    void SetParam(string name, GeaValue value);

    void SetParamExpr(string name, string expression);

    GeaValue GetParam(string name, GeaType type);

    /// <summary>Runs a builder; returns the outputs by name.</summary>
    IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs);

    /// <summary>
    /// ADD_COMPONENT (TARGET ASSEMBLY): inserts a part at a position (mm) with a rotation (degrees) and returns a reference to the occurrence.
    /// A host that builds a part (not an assembly) does not support it, hence the default that throws GEA4020.
    /// </summary>
    GeaRef AddComponent(string part, double[] positionMm, double[] rotationDeg) =>
        throw new GeaRuntimeException("GEA4020", "ADD_COMPONENT needs an assembly; this host builds a part.");

    void SetRefName(GeaRef target, string name);

    string GetRefName(GeaRef target);

    void SetRefAttr(GeaRef target, string key, GeaValue value);

    GeaValue? GetRefAttr(GeaRef target, string key);

    void SetAppearance(GeaRef target, string appearance);

    void Regen();

    void Progress(GeaProgressKind kind, string? text, double? percent);

    void Print(string message, int line);
}

public static class GeaNames
{
    /// <summary>
    /// Naming rule of SET_REF_NAME: a name ending in <c>*</c> gets a suffix _1, _2... to make it unique
    /// (<c>"TR_Tank*"</c> becomes <c>TR_Tank_1</c>). Without <c>*</c>, a duplicate is error GEA4011.
    /// </summary>
    public static string Resolve(string pattern, Func<string, bool> taken)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            throw new GeaRuntimeException("GEA4011", "An object name must not be empty.");
        if (!pattern.EndsWith("*", StringComparison.Ordinal))
        {
            if (taken(pattern)) throw new GeaRuntimeException("GEA4011", $"The name '{pattern}' is already used by another object. Add '*' at the end to number it automatically.");
            return pattern;
        }
        var stem = pattern.TrimEnd('*').TrimEnd('_');
        for (int i = 1; i < 100000; i++)
        {
            var candidate = $"{stem}_{i}";
            if (!taken(candidate)) return candidate;
        }
        throw new GeaRuntimeException("GEA4011", $"Could not find a unique name for '{pattern}'.");
    }
}

/// <summary>
/// Simulation Host: a virtual model in memory that records a trace of the commands. Used to dry-run BUILD (catching run-time errors) before touching Inventor,
/// and for tests. It does not reproduce real geometry.
/// </summary>
public sealed class GeaSimulationHost : IGeaHost
{
    public sealed class SimObject
    {
        public string Name { get; set; } = "";
        public string Builder { get; init; } = "";
        public Dictionary<string, GeaValue> Attributes { get; } = new(StringComparer.Ordinal);
        public string? Appearance { get; set; }
        public IReadOnlyDictionary<string, GeaValue> Inputs { get; init; } = new Dictionary<string, GeaValue>();
    }

    private int _bodyCounter;
    private int _componentCounter;

    public Dictionary<string, GeaValue> Parameters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ParameterExpressions { get; } = new(StringComparer.Ordinal);
    public List<SimObject> Objects { get; } = new();
    public List<string> Trace { get; } = new();

    public void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> values)
    {
        foreach (var (k, v) in values) Parameters[k] = v;
        Trace.Add($"SYNC {values.Count} parameters");
    }

    public void SetParam(string name, GeaValue value)
    {
        Parameters[name] = value;
        Trace.Add($"SET_PARAM {name} = {value}");
    }

    public void SetParamExpr(string name, string expression)
    {
        ParameterExpressions[name] = expression;
        Trace.Add($"SET_PARAM_EXPR {name} = \"{expression}\"");
    }

    public GeaValue GetParam(string name, GeaType type)
    {
        if (!Parameters.TryGetValue(name, out var v))
            throw new GeaRuntimeException("GEA4099", $"Parameter '{name}' does not exist.");
        if (v.Type == type) return v;
        if (v.IsNumeric && type is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle) return GeaValue.Number(type, v.Num);
        throw new GeaRuntimeException("GEA2002", $"Parameter '{name}' is {GeaUnits.TypeName(v.Type)}, it cannot be read as {GeaUnits.TypeName(type)}.");
    }

    public IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs)
    {
        var obj = new SimObject { Name = $"Body{++_bodyCounter}", Builder = builder.Name, Inputs = inputs };
        Objects.Add(obj);
        Trace.Add($"CREATE_FEATURE builder:{builder.Name} " + string.Join(", ", inputs.Select(kv => $"{kv.Key}={kv.Value}")));
        var props = new Dictionary<string, GeaValue>(inputs, StringComparer.Ordinal);
        var result = builder.Outputs.ToDictionary(o => o.Name, o => new GeaRef("BODY", obj, builder.Name, props));
        // Same as the Inventor Host: a separate turret is another body
        if (builder.Name == "Bushing" && inputs.TryGetValue("SeparateTurret", out var sep) && sep.Bool)
        {
            var turret = new SimObject { Name = $"Body{++_bodyCounter}", Builder = builder.Name + ".Turret", Inputs = inputs };
            Objects.Add(turret);
            result["Turret"] = new GeaRef("BODY", turret, builder.Name, props);
        }
        return result;
    }

    public GeaRef AddComponent(string part, double[] positionMm, double[] rotationDeg)
    {
        var inputs = new Dictionary<string, GeaValue>(StringComparer.Ordinal)
        {
            ["Part"] = GeaValue.Text(part),
            ["X"] = GeaValue.Length(positionMm[0]), ["Y"] = GeaValue.Length(positionMm[1]), ["Z"] = GeaValue.Length(positionMm[2]),
            ["Rx"] = GeaValue.Angle(rotationDeg[0]), ["Ry"] = GeaValue.Angle(rotationDeg[1]), ["Rz"] = GeaValue.Angle(rotationDeg[2]),
        };
        var obj = new SimObject { Name = $"Comp{++_componentCounter}", Builder = "component:" + part, Inputs = inputs };
        Objects.Add(obj);
        Trace.Add($"ADD_COMPONENT \"{part}\" AT {inputs["X"]}, {inputs["Y"]}, {inputs["Z"]} ROT {inputs["Rx"]}, {inputs["Ry"]}, {inputs["Rz"]}");
        return new GeaRef("COMPONENT", obj, "component", inputs);
    }

    private static SimObject Obj(GeaRef r) => (SimObject)r.Handle;

    public void SetRefName(GeaRef target, string name)
    {
        var self = Obj(target);
        self.Name = GeaNames.Resolve(name, n => Objects.Any(o => o != self && o.Name == n));
        Trace.Add($"SET_REF_NAME {self.Name}");
    }

    public string GetRefName(GeaRef target) => Obj(target).Name;

    public void SetRefAttr(GeaRef target, string key, GeaValue value)
    {
        Obj(target).Attributes[key] = value;
        Trace.Add($"SET_REF_ATTR {Obj(target).Name} {key} = {value}");
    }

    public GeaValue? GetRefAttr(GeaRef target, string key) =>
        Obj(target).Attributes.TryGetValue(key, out var v) ? v : null;

    public void SetAppearance(GeaRef target, string appearance)
    {
        Obj(target).Appearance = appearance;
        Trace.Add($"SET_APPEARANCE {Obj(target).Name} {appearance}");
    }

    public void Regen() => Trace.Add("REGEN_MDL");

    public void Progress(GeaProgressKind kind, string? text, double? percent) =>
        Trace.Add($"PROCESSING_BOX_{kind.ToString().ToUpperInvariant()} {text} {percent}");

    public void Print(string message, int line) => Trace.Add($"PRINT ({line}) {message}");
}
