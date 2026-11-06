using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Gea;

public enum GeaProgressKind { Start, State, End }

/// <summary>
/// Giao diện Host (mục 14 của mapping, mục 14 của đặc tả). Trình thông dịch gọi Host cho mọi việc đụng tới model.
/// Hai cài đặt: Inventor Host (add-in, worker) và <see cref="GeaSimulationHost"/> (chạy thử, test).
/// Lỗi của Host ném <see cref="GeaRuntimeException"/> mã GEA4xxx; ngoại lệ khác được bọc thành GEA4099.
/// </summary>
public interface IGeaHost
{
    /// <summary>Đồng bộ mọi thông số FORM thành User Parameter trước dòng đầu tiên của BUILD (mục 9.1).</summary>
    void SyncParameters(IReadOnlyList<KeyValuePair<string, GeaValue>> values);

    void SetParam(string name, GeaValue value);

    void SetParamExpr(string name, string expression);

    GeaValue GetParam(string name, GeaType type);

    /// <summary>Chạy builder; trả về các đầu ra theo tên.</summary>
    IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs);

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
    /// Quy tắc tên của SET_REF_NAME: tên kết thúc bằng <c>*</c> thì thêm hậu tố _1, _2... để duy nhất
    /// (<c>"TR_Tank*"</c> thành <c>TR_Tank_1</c>). Không có <c>*</c> mà trùng thì lỗi GEA4011.
    /// </summary>
    public static string Resolve(string pattern, Func<string, bool> taken)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            throw new GeaRuntimeException("GEA4011", "Tên đối tượng không được rỗng.");
        if (!pattern.EndsWith("*", StringComparison.Ordinal))
        {
            if (taken(pattern)) throw new GeaRuntimeException("GEA4011", $"Tên '{pattern}' đã có đối tượng khác dùng. Thêm '*' ở cuối để tự đánh số.");
            return pattern;
        }
        var stem = pattern.TrimEnd('*').TrimEnd('_');
        for (int i = 1; i < 100000; i++)
        {
            var candidate = $"{stem}_{i}";
            if (!taken(candidate)) return candidate;
        }
        throw new GeaRuntimeException("GEA4011", $"Không tìm được tên duy nhất cho '{pattern}'.");
    }
}

/// <summary>
/// Mô phỏng Host: model ảo trong bộ nhớ, ghi vết lệnh. Dùng để chạy thử BUILD (bắt lỗi thực thi) trước khi đụng tới Inventor,
/// và để test. Không tái tạo hình học thật.
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
            throw new GeaRuntimeException("GEA4099", $"Parameter '{name}' không tồn tại.");
        if (v.Type == type) return v;
        if (v.IsNumeric && type is GeaType.Integer or GeaType.Double or GeaType.Length or GeaType.Angle) return GeaValue.Number(type, v.Num);
        throw new GeaRuntimeException("GEA2002", $"Parameter '{name}' là {GeaUnits.TypeName(v.Type)}, không đọc được thành {GeaUnits.TypeName(type)}.");
    }

    public IReadOnlyDictionary<string, GeaRef> CreateFeature(BuilderManifest builder, GeaRef? on, IReadOnlyDictionary<string, GeaValue> inputs)
    {
        var obj = new SimObject { Name = $"Body{++_bodyCounter}", Builder = builder.Name, Inputs = inputs };
        Objects.Add(obj);
        Trace.Add($"CREATE_FEATURE builder:{builder.Name} " + string.Join(", ", inputs.Select(kv => $"{kv.Key}={kv.Value}")));
        var props = new Dictionary<string, GeaValue>(inputs, StringComparer.Ordinal);
        var result = builder.Outputs.ToDictionary(o => o.Name, o => new GeaRef("BODY", obj, builder.Name, props));
        // Giống Inventor Host: chân sứ tách riêng là một body khác
        if (builder.Name == "Bushing" && inputs.TryGetValue("SeparateTurret", out var sep) && sep.Bool)
        {
            var turret = new SimObject { Name = $"Body{++_bodyCounter}", Builder = builder.Name + ".Turret", Inputs = inputs };
            Objects.Add(turret);
            result["Turret"] = new GeaRef("BODY", turret, builder.Name, props);
        }
        return result;
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
