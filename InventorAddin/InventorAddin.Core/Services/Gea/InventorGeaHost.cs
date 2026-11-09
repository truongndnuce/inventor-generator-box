using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InventorAddin.Gea;
using Inv = Inventor;

namespace InventorAddin.Services.Gea;

/// <summary>Dấu sở hữu: mọi đối tượng do một script .gea tạo trong Part mang AttributeSet "GEA_Owner" với mã ràng buộc của Part.</summary>
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
/// Inventor Host của .gea: đồng bộ User Parameter, chạy builder C# bằng Inventor API, đặt tên, gắn thuộc tính, ngoại hình.
/// Chạy bên trong Transaction do <see cref="GeaDocumentService"/> mở; mọi đối tượng tạo ra đều gắn dấu sở hữu.
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

    // ================================================================ thông số

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
        throw new GeaRuntimeException("GEA4099", $"Không tạo được Parameter {name} = \"{expression}\": {last?.Message}");
    }

    public GeaValue GetParam(string name, GeaType type)
    {
        Inv.Parameter p;
        try { p = _def.Parameters[name]; }
        catch { throw new GeaRuntimeException("GEA4099", $"Parameter '{name}' không tồn tại trong Part."); }

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
        throw new GeaRuntimeException("GEA2002", $"Parameter '{name}' không đọc được thành {GeaUnits.TypeName(type)}.");
    }

    /// <summary>Tạo hoặc cập nhật User Parameter theo kiểu của giá trị. Parameter trùng tên mà không do script này tạo: GEA4010.</summary>
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
                // Kiểu thông số đổi (ví dụ từ LENGTH sang STRING): xóa rồi tạo lại
                try { p.Delete(); }
                catch (Exception ex)
                {
                    throw new GeaRuntimeException("GEA4099", $"Parameter {name} đổi kiểu nhưng không xóa được để tạo lại: {ex.Message}");
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
                // Một số phiên bản không có Parameter kiểu chữ/logic: không chặn việc dựng
                Warnings.Add($"Không tạo được Parameter {name} ({GeaUnits.TypeName(value.Type)}): {ex.Message}");
                return;
            }
            throw new GeaRuntimeException("GEA4099", $"Không tạo được Parameter {name}: {ex.Message}");
        }
        GeaOwnership.Own(p, _binding);
    }

    /// <summary>Parameter của script này (nếu có). Null nếu chưa có. Trùng tên với Parameter khác: GEA4010.</summary>
    private Inv.UserParameter? OwnedOrNewParam(string name)
    {
        _touchedParams.Add(name);
        Inv.UserParameter? found = null;
        foreach (Inv.UserParameter p in _def.Parameters.UserParameters)
        {
            if (p.Name == name) { found = p; break; }
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new GeaRuntimeException("GEA4010", $"Đã có Parameter '{p.Name}' chỉ khác '{name}' ở chữ hoa thường.");
        }
        if (found == null)
        {
            // Trùng với Parameter của model (d0, d1...) hoặc tham chiếu
            try
            {
                var any = _def.Parameters[name];
                if (any != null) throw new GeaRuntimeException("GEA4010", $"Tên '{name}' đã được một Parameter khác trong Part dùng.");
            }
            catch (GeaRuntimeException) { throw; }
            catch { /* không có: được phép tạo */ }
            return null;
        }
        if (!GeaOwnership.IsOwned(found, _binding))
            throw new GeaRuntimeException("GEA4010",
                $"Parameter '{name}' đã có trong Part nhưng không do script này tạo. Không ghi đè; đổi tên thông số hoặc xóa Parameter đó.");
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
        _ => throw new GeaRuntimeException("GEA2002", "Không lưu tham chiếu thành Parameter."),
    };

    /// <summary>Xóa Parameter do script này tạo ở lần trước mà lần này không còn khai báo.</summary>
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
            catch (Exception ex) { Warnings.Add($"Parameter {name} không còn trong script nhưng không xóa được (đang được dùng?): {ex.Message}"); }
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
                Require(len > 0 && wid > 0 && hgt > 0, "Tank: Length, Width, Height phải dương.");
                Require(wall > 0 && 2 * wall < Math.Min(len, wid), "Tank: Wall phải dương và nhỏ hơn nửa cạnh ngắn.");
                Require(bottom > 0 && bottom < hgt, "Tank: Bottom phải dương và nhỏ hơn Height.");
                Require(cr >= 0 && cr <= Math.Min(len, wid) / 2, "Tank: CornerRadius không vượt quá nửa cạnh ngắn.");
                Require(skirt >= 0, "Tank: SkirtHeight không âm.");
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
                    throw new GeaRuntimeException("GEA4099", "Cover: đầu vào Tank phải là thùng do builder:Tank tạo.");
                double TP(string k) => tank.Props.TryGetValue(k, out var v) ? v.Num : 0;
                double len = TP("Length"), wid = TP("Width"), wall = TP("Wall"), cr = TP("CornerRadius"), ir = TP("InnerRadius");
                double top = TP("Top");
                double thick = L("Thickness"), lip = L("Lip"), clr = L("Clearance"), gap = L("Gap");
                Require(thick > 0, "Cover: Thickness phải dương.");
                Require(lip >= 0 && clr >= 0 && gap >= 0, "Cover: Lip, Clearance, Gap không âm.");
                double lipSx = len - 2 * wall - 2 * clr, lipSd = wid - 2 * wall - 2 * clr;
                Require(lip <= Eps || (lipSx > 0 && lipSd > 0), "Cover: khe hở quá lớn, gờ nắp không còn chỗ.");

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
                Require(sx > 0 && sd > 0 && h > 0, "Block: SizeX, SizeD, Height phải dương.");
                built = _geo.Prism(tag, L("CenterX"), L("CenterD"), L("Elevation"), sx, sd, L("CornerRadius"), h);
                break;
            }
            case "Cylinder":
            {
                double dia = L("Diameter"), len = L("Length");
                Require(dia > 0 && len > 0, "Cylinder: Diameter, Length phải dương.");
                var axis = (inputs["Axis"].Str ?? "D").Trim().ToUpperInvariant();
                try { built = _geo.Cylinder(tag, axis, L("CenterX"), L("CenterD"), L("Elevation"), dia, len); }
                catch (ArgumentException ex) { throw new GeaRuntimeException("GEA4099", "Cylinder: " + ex.Message); }
                break;
            }
            case "Bushing":
            {
                double dia = L("Diameter"), len = L("Length"), turret = L("TurretHeight");
                int sheds = (int)Math.Round(inputs["ShedCount"].Num);
                Require(dia > 0 && len > 0 && turret > 0, "Bushing: Diameter, Length, TurretHeight phải dương.");
                Require(sheds >= 1 && sheds <= 100, "Bushing: ShedCount phải từ 1 đến 100.");
                Require(L("TurretDiameter") >= 0 && L("TurretTaper") >= 0 && L("CapHeight") >= 0,
                    "Bushing: TurretDiameter, TurretTaper, CapHeight không âm.");
                try
                {
                    bool separate = inputs["SeparateTurret"].Bool;
                    var (porcelain, turretBody) = _geo.Bushing(tag, L("CenterX"), L("CenterD"), L("Elevation"), dia, len, turret, sheds,
                        L("TurretDiameter"), L("TurretTaper"), L("CapHeight"), separate);
                    built = porcelain;
                    if (separate)
                    {
                        try { turretBody.Body.Name = tag + "_Turret"; } catch { /* để tên mặc định */ }
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
                Require(sx > 0 && sd > 0 && h > 0, "Radiator: SizeX, SizeD, Height phải dương.");
                Require(fins >= 1 && fins <= 500, "Radiator: FinCount phải từ 1 đến 500.");
                Require(fin > 0 && header > 0 && end > 0, "Radiator: FinThickness, HeaderHeight, EndPlate phải dương.");
                Require(2 * header < h, "Radiator: hai hộp góp cao hơn cả cánh tản nhiệt.");
                double inner = sx - 2 * end;
                Require(inner > fins * fin * 1.2, $"Radiator: {fins} lá dày {fin} mm không vừa bề rộng trong {inner:0.#} mm (cần khe hở giữa các lá).");
                built = _geo.Radiator(tag, L("CenterX"), L("CenterD"), L("Elevation"), sx, sd, h, fins, fin, header, end);
                break;
            }
            default:
                throw new GeaRuntimeException("GEA2013", $"Inventor Host chưa có builder '{builder.Name}'.");
        }

        try { built.Body.Name = tag; } catch { /* để tên mặc định */ }
        var r = new GeaRef("BODY", built, builder.Name, props);
        // Đầu ra phụ (ví dụ chân sứ tách riêng) là body khác; đầu ra không có body riêng trỏ về body chính
        return builder.Outputs.ToDictionary(o => o.Name,
            o => extra.TryGetValue(o.Name, out var b) ? new GeaRef("BODY", b, builder.Name, props) : r);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new GeaRuntimeException("GEA4099", message);
    }

    private static PartGeometry.Built Built(GeaRef r) =>
        r.Handle as PartGeometry.Built ?? throw new GeaRuntimeException("GEA4012", "Tham chiếu không thuộc model Inventor hiện tại.");

    // ================================================================ tên và thuộc tính

    public void SetRefName(GeaRef target, string name)
    {
        var built = Built(target);
        var body = built.Body;
        string current = body.Name;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Inv.SurfaceBody b in _def.SurfaceBodies) names.Add(b.Name);

        var resolved = GeaNames.Resolve(name, n => !string.Equals(n, current, StringComparison.OrdinalIgnoreCase) && names.Contains(n));
        body.Name = resolved;

        // Đổi tên sketch, feature đi kèm cho dễ đọc trong cây trình duyệt
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
            catch { /* trùng tên: giữ tên cũ */ }
        }
    }

    public string GetRefName(GeaRef target) => Built(target).Body.Name;

    public void SetRefAttr(GeaRef target, string key, GeaValue value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new GeaRuntimeException("GEA4099", "Khóa thuộc tính không được rỗng.");
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

    // ================================================================ ngoại hình

    /// <summary>Tên quen thuộc → mã appearance trong thư viện của Inventor (thử trước khi tạo màu riêng).</summary>
    private static readonly Dictionary<string, string[]> LibraryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grey"] = new[] { "Plastic-010" },       // "Smooth - Light Gray"
        ["Gray"] = new[] { "Plastic-010" },
        ["Porcelain"] = new[] { "Fabric-006" },   // "Dark Brown": nâu sẫm như sứ
    };

    /// <summary>Màu dự phòng (RGB) khi thư viện không có: tạo appearance "Generic" ngay trong Part.</summary>
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
            catch { /* bỏ qua */ }
            return null;
        }

        if (!hasAlias && isColor)
        {
            // Màu đặt tên hoặc #RRGGBB không có bí danh: luôn dùng màu đục do add-in tạo. Không tìm theo tên trong thư viện,
            // vì thư viện có appearance trùng tên nhưng khác hẳn (ví dụ "Green" là kính trong suốt).
            // Inventor bỏ qua tên truyền vào Assets.Add (tự đặt "1:<DisplayName>"), nên nhận ra màu do add-in tạo bằng
            // tên hiển thị và danh mục rỗng (asset chép từ thư viện luôn có danh mục).
            found = InPart(a => (string.Equals(a.DisplayName, name, StringComparison.OrdinalIgnoreCase) && IsOwnColor(a))
                                || string.Equals(a.DisplayName, name + " (GEA)", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // 1. Đã có trong Part (theo tên hiển thị hoặc mã)
            found = InPart(a => string.Equals(a.DisplayName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // 2. Thư viện của Inventor: theo bí danh hoặc theo mã
        if (found == null && (hasAlias || !isColor))
        {
            var ids = hasAlias ? alias!.Append(name) : new[] { name };
            foreach (var id in ids)
            {
                foreach (Inv.AssetLibrary lib in _app.AssetLibraries)
                {
                    Inv.Asset? a = null;
                    try { a = lib.AppearanceAssets[id]; } catch { /* thư viện này không có */ }
                    if (a == null) continue;
                    try { found = a.CopyTo(doc, true); } catch { found = null; }
                    if (found != null) break;
                }
                if (found != null) break;
            }
        }

        // 3. Màu đặt tên hoặc #RRGGBB: tạo appearance Generic trong Part
        if (found == null && isColor)
        {
            try
            {
                Inv.Asset asset;
                try { asset = _part.Assets.Add(Inv.AssetTypeEnum.kAssetTypeAppearance, "Generic", colorId, name); }
                catch
                {
                    // Tên hiển thị đã bị một appearance khác (ví dụ chép từ thư viện) chiếm: dùng tên riêng
                    asset = _part.Assets.Add(Inv.AssetTypeEnum.kAssetTypeAppearance, "Generic", colorId, name + " (GEA)");
                }
                var diffuse = (Inv.ColorAssetValue)asset["generic_diffuse"];
                diffuse.Value = _app.TransientObjects.CreateColor(rgb.r, rgb.g, rgb.b);
                found = asset;
            }
            catch (Exception ex)
            {
                Warnings.Add($"Không tạo được appearance '{name}': {ex.Message}");
            }
        }

        if (found == null)
            throw new GeaRuntimeException("GEA4020",
                $"Không có ngoại hình '{name}'. Dùng tên trong thư viện Inventor, mã #RRGGBB, hoặc một màu: {string.Join(", ", Colors.Keys)}.");
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

    // ================================================================ khác

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
