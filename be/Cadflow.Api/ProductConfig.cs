using System.Globalization;
using System.Text.Json;

namespace Cadflow.Api;

/// <summary>Một ô nhập của nhóm Operation / CAD design condition (giao diện dựng form và ma trận từ danh sách này).</summary>
public sealed record ConfigField(string Name, string Label, string Type, string[]? Options = null, string Default = "", string Unit = "");

public sealed record ConfigGroup(string Key, string Label, ConfigField[] Fields);

/// <summary>Các nhóm thông tin của một Configuration ngoài giá trị thông số (component): Operation và CAD design condition.</summary>
public static class ConfigSchema
{
    public static readonly string[] Statuses = { "draft", "active", "deprecated" };

    public static readonly ConfigGroup[] Groups =
    {
        new("operation", "Operation", new ConfigField[]
        {
            new("mode", "Operating mode", "select", new[] { "Continuous", "Intermittent", "Standby" }, "Continuous"),
            new("frequency", "Rated frequency", "select", new[] { "50 Hz", "60 Hz" }, "50 Hz"),
            new("ambientMin", "Ambient temperature, min", "number", Default: "-10", Unit: "°C"),
            new("ambientMax", "Ambient temperature, max", "number", Default: "40", Unit: "°C"),
            new("altitude", "Site altitude", "number", Default: "1000", Unit: "m"),
            new("installation", "Installation", "select", new[] { "Indoor", "Outdoor" }, "Outdoor"),
            new("designLife", "Design life", "number", Default: "25", Unit: "years"),
        }),
        new("cad", "CAD design condition", new ConfigField[]
        {
            new("standard", "Drawing standard", "select", new[] { "ISO", "ANSI", "DIN", "JIS", "GB" }, "ISO"),
            new("projection", "Projection", "select", new[] { "First angle", "Third angle" }, "First angle"),
            new("units", "Units", "select", new[] { "mm", "inch" }, "mm"),
            new("sheet", "Sheet size", "select", new[] { "A0", "A1", "A2", "A3", "A4" }, "A3"),
            new("scale", "View scale", "select", new[] { "1:1", "1:2", "1:5", "1:10", "1:20", "1:50", "1:100" }, "1:10"),
            new("tolerance", "General tolerance", "select", new[] { "ISO 2768-f", "ISO 2768-m", "ISO 2768-c" }, "ISO 2768-m"),
            new("exports", "Export formats", "multi", new[] { "PDF", "DWG", "STEP" }, "PDF,DWG,STEP"),
        }),
    };

    /// <summary>Chuẩn hóa giá trị người dùng gửi: bỏ khóa lạ, điền mặc định, kiểm tra lựa chọn và số. Trả lỗi nếu sai.</summary>
    public static Dictionary<string, string> Normalize(ConfigGroup group, Dictionary<string, string>? input, out string? error)
    {
        error = null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in group.Fields)
        {
            var v = input != null && input.TryGetValue(f.Name, out var x) ? (x ?? "").Trim() : f.Default;
            switch (f.Type)
            {
                case "number":
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    { error = $"{group.Label}: '{f.Label}' must be a number."; return result; }
                    v = d.ToString(CultureInfo.InvariantCulture);
                    break;
                case "select":
                    if (!f.Options!.Contains(v)) { error = $"{group.Label}: '{f.Label}' must be one of {string.Join(", ", f.Options!)}."; return result; }
                    break;
                case "multi":
                    var picked = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
                    if (picked.Length == 0 || picked.Any(p => !f.Options!.Contains(p))) { error = $"{group.Label}: '{f.Label}' needs at least one of {string.Join(", ", f.Options!)}."; return result; }
                    v = string.Join(",", f.Options!.Where(picked.Contains));
                    break;
            }
            result[f.Name] = v;
        }
        return result;
    }

    public static Dictionary<string, string> Defaults(ConfigGroup g) => g.Fields.ToDictionary(f => f.Name, f => f.Default);
}

/// <summary>
/// Một Configuration của sản phẩm: tên, trạng thái, điều kiện vận hành, điều kiện thiết kế CAD và giá trị cho mọi thông số
/// (component). Đơn đặt hàng chọn một Configuration; thông số đơn không nêu thì lấy từ đây.
/// </summary>
public sealed class ProductConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "draft"; // draft | active | deprecated
    public int Revision { get; set; } = 1;
    public bool IsDefault { get; set; }           // Configuration dùng khi đơn không chọn cái nào
    public Dictionary<string, string> Operation { get; set; } = new();
    public Dictionary<string, string> Cad { get; set; } = new();
    public Dictionary<string, double> Values { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed record ConfigRequest(string? Name, string? Description, string? Status, bool? IsDefault,
    Dictionary<string, string>? Operation, Dictionary<string, string>? Cad, Dictionary<string, double>? Values, string? UpdatedBy);

/// <summary>Lưu các Configuration ở data\config\&lt;mã sản phẩm&gt;.configs.json (một file mỗi sản phẩm, ghi nguyên tử).</summary>
public sealed class ConfigStore
{
    private readonly string _dir;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ConfigStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "config");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string code) => Path.Combine(_dir, code.ToLowerInvariant() + ".configs.json");

    private List<ProductConfig> Load(Product product)
    {
        var path = PathFor(product.Code);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<ProductConfig>>(File.ReadAllText(path), Opts) is { } list) return list;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* file hỏng: tạo lại bản mẫu */ }
        // Lần đầu: một Configuration mẫu (mặc định gốc) để danh sách không trống và đơn luôn có chỗ lấy giá trị.
        var now = DateTimeOffset.UtcNow;
        var seed = new List<ProductConfig>
        {
            new()
            {
                Id = NewId(), Name = $"{product.Code}-1.0", Description = "Factory defaults", Status = "active", IsDefault = true,
                Operation = ConfigSchema.Defaults(ConfigSchema.Groups[0]), Cad = ConfigSchema.Defaults(ConfigSchema.Groups[1]),
                Values = product.Complete(null), CreatedAt = now, UpdatedAt = now, UpdatedBy = "system",
            },
        };
        Write(product, seed);
        return seed;
    }

    private void Write(Product product, List<ProductConfig> list)
    {
        var tmp = PathFor(product.Code) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Opts));
        File.Move(tmp, PathFor(product.Code), overwrite: true);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];

    public List<ProductConfig> List(Product product) { lock (_lock) return Load(product); }

    public ProductConfig? Get(Product product, string id) { lock (_lock) return Load(product).FirstOrDefault(c => c.Id == id); }

    /// <summary>Configuration mặc định của sản phẩm (null nếu đã bỏ cờ mặc định hết).</summary>
    public ProductConfig? Default(Product product) { lock (_lock) return Load(product).FirstOrDefault(c => c.IsDefault); }

    /// <summary>Tạo (id null) hoặc sửa. Trả (config, lỗi). Mọi kiểm tra nằm ở đây để API và test dùng chung.</summary>
    public (ProductConfig? Config, string? Error, bool NotFound) Save(Product product, string? id, ConfigRequest req)
    {
        lock (_lock)
        {
            var list = Load(product);
            var existing = id == null ? null : list.FirstOrDefault(c => c.Id == id);
            if (id != null && existing == null) return (null, "Unknown configuration.", true);

            var name = (req.Name ?? existing?.Name ?? "").Trim();
            if (name.Length is 0 or > 80) return (null, "The configuration needs a name of 1-80 characters.", false);
            if (list.Any(c => c.Id != id && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"A configuration named '{name}' already exists for this product.", false);

            var status = (req.Status ?? existing?.Status ?? "draft").ToLowerInvariant();
            if (!ConfigSchema.Statuses.Contains(status)) return (null, $"Status must be one of {string.Join(", ", ConfigSchema.Statuses)}.", false);

            var op = ConfigSchema.Normalize(ConfigSchema.Groups[0], req.Operation ?? existing?.Operation, out var e1);
            if (e1 != null) return (null, e1, false);
            var cad = ConfigSchema.Normalize(ConfigSchema.Groups[1], req.Cad ?? existing?.Cad, out var e2);
            if (e2 != null) return (null, e2, false);

            var values = product.Complete(req.Values ?? existing?.Values);
            var issues = product.Validate(values);
            if (issues.Count > 0) return (null, "Component values violate product rules: " + issues[0].Message, false);

            var isDefault = req.IsDefault ?? existing?.IsDefault ?? false;
            if (isDefault && status != "active") return (null, "Only an active configuration can be the default.", false);

            var now = DateTimeOffset.UtcNow;
            var by = string.IsNullOrWhiteSpace(req.UpdatedBy) ? "anonymous" : req.UpdatedBy.Trim();
            var cfg = existing ?? new ProductConfig { Id = NewId(), CreatedAt = now };
            if (existing == null) list.Add(cfg); else cfg.Revision++;
            cfg.Name = name; cfg.Description = (req.Description ?? existing?.Description ?? "").Trim();
            cfg.Status = status; cfg.Operation = op; cfg.Cad = cad; cfg.Values = values;
            cfg.IsDefault = isDefault; cfg.UpdatedAt = now; cfg.UpdatedBy = by.Length > 80 ? by[..80] : by;
            if (isDefault) foreach (var o in list.Where(c => c != cfg)) o.IsDefault = false;
            // Khi một Configuration mặc định bị chuyển sang trạng thái khác active thì mất cờ mặc định.
            if (status != "active") cfg.IsDefault = false;
            Write(product, list);
            return (cfg, null, false);
        }
    }

    public bool Delete(Product product, string id)
    {
        lock (_lock)
        {
            var list = Load(product);
            if (list.RemoveAll(c => c.Id == id) == 0) return false;
            Write(product, list);
            return true;
        }
    }

    /// <summary>Giá trị của Configuration (chỉ định hoặc mặc định), rồi giá trị trong đơn đè lên; thiếu thì <c>Complete</c> điền mặc định gốc.</summary>
    public Dictionary<string, double> Effective(Product product, IReadOnlyDictionary<string, double>? partial, ProductConfig? config)
    {
        var merged = new Dictionary<string, double>(config?.Values ?? Default(product)?.Values ?? new(), StringComparer.Ordinal);
        if (partial != null) foreach (var kv in partial) merged[kv.Key] = kv.Value;
        return product.Complete(merged);
    }
}
