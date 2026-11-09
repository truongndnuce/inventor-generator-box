using System;
using System.Collections.Generic;
using System.Linq;

namespace InventorAddin.Gea;

/// <summary>Một đầu vào hoặc đầu ra của builder (mục 12 của đặc tả).</summary>
public sealed record BuilderPort(string Name, GeaType Type, bool Required = true, GeaValue? Default = null, string Description = "");

/// <summary>
/// Manifest của builder: hợp đồng giữa script và hàm dựng C#. Bước kiểm tra ngữ nghĩa dùng manifest để kiểm tra
/// CREATE_FEATURE mà không cần Inventor; Inventor Host chạy hàm dựng thật cùng tên.
/// </summary>
public sealed record BuilderManifest(
    string Name, string Version, string Target, string Description,
    IReadOnlyList<BuilderPort> Inputs, IReadOnlyList<BuilderPort> Outputs)
{
    public BuilderPort? Input(string name) => Inputs.FirstOrDefault(p => p.Name == name);

    public BuilderPort? Output(string name) => Outputs.FirstOrDefault(p => p.Name == name);
}

/// <summary>
/// Danh mục builder dựng sẵn. Hệ tọa độ chung (giống mô hình máy biến áp): X dọc chiều dài, "D" theo chiều sâu (trục Z của Inventor),
/// chiều cao theo trục Y của Inventor ("Elevation" là cao độ đáy, mặt đất là 0). Mọi kích thước tính bằng mm.
/// </summary>
public static class GeaBuilderCatalog
{
    private static readonly GeaValue Zero = GeaValue.Length(0);

    private static BuilderPort L(string name, string description, bool required = true) =>
        new(name, GeaType.Length, required, required ? null : Zero, description);

    private static readonly BuilderPort BodyOut = new("Body", GeaType.Reference, Description: "Solid body vừa tạo");

    public static readonly IReadOnlyList<BuilderManifest> All = new[]
    {
        new BuilderManifest("Tank", "1.0", "PART",
            "Thùng rỗng mở nắp: hộp bo góc đứng, thành dày Wall, đáy dày Bottom, chân đế (skirt) phía dưới. Tâm thùng ở gốc.",
            new[]
            {
                L("Length", "Chiều dài ngoài (X)"), L("Width", "Chiều rộng ngoài (D)"), L("Height", "Chiều cao thân (chưa tính chân đế)"),
                L("Wall", "Độ dày thành"), L("Bottom", "Độ dày đáy"),
                L("CornerRadius", "Bán kính bo góc ngoài", false), L("InnerRadius", "Bán kính bo góc trong", false),
                L("SkirtHeight", "Chiều cao chân đế", false),
            },
            new[] { BodyOut }),

        new BuilderManifest("Cover", "1.0", "PART",
            "Nắp phẳng đặt trên miệng thùng, có gờ lồng vào lòng thùng (cách thành trong một khe hở).",
            new[]
            {
                new BuilderPort("Tank", GeaType.Reference, true, null, "Thùng do builder:Tank tạo"),
                L("Thickness", "Độ dày nắp"), L("Lip", "Chiều cao gờ"), L("Clearance", "Khe hở giữa gờ và thành trong", false),
                L("Gap", "Khoảng nâng nắp lên khỏi miệng thùng", false),
            },
            new[] { BodyOut }),

        new BuilderManifest("Block", "1.0", "PART",
            "Khối hộp chữ nhật (có thể bo góc đứng).",
            new[]
            {
                L("CenterX", "Tâm theo X", false), L("CenterD", "Tâm theo chiều sâu", false), L("Elevation", "Cao độ mặt đáy", false),
                L("SizeX", "Kích thước theo X"), L("SizeD", "Kích thước theo chiều sâu"), L("Height", "Chiều cao"),
                L("CornerRadius", "Bán kính bo góc đứng", false),
            },
            new[] { BodyOut }),

        new BuilderManifest("Cylinder", "1.0", "PART",
            "Khối trụ. Axis \"D\" (mặc định): nằm ngang theo chiều sâu, đối xứng quanh CenterD; \"X\": nằm ngang theo X, đối xứng quanh CenterX; " +
            "\"H\": đứng, đáy ở Elevation. Với trụ nằm ngang, Elevation là cao độ của trục.",
            new[]
            {
                L("CenterX", "Tâm theo X", false), L("CenterD", "Tâm theo chiều sâu", false), L("Elevation", "Cao độ trục (ngang) hoặc đáy (đứng)", false),
                L("Diameter", "Đường kính"), L("Length", "Chiều dài trụ"),
                new BuilderPort("Axis", GeaType.String, false, GeaValue.Text("D"), "D, X hoặc H"),
            },
            new[] { BodyOut }),

        new BuilderManifest("Bushing", "1.0", "PART",
            "Sứ xuyên đứng: chân sứ (turret) hình trụ và thân sứ có các tán thu nhỏ dần lên trên. Một solid tròn xoay.",
            new[]
            {
                L("CenterX", "Tâm theo X", false), L("CenterD", "Tâm theo chiều sâu", false), L("Elevation", "Cao độ chân sứ", false),
                L("Diameter", "Đường kính tán lớn nhất"), L("Length", "Chiều dài thân sứ (trên chân sứ)"),
                L("TurretHeight", "Chiều cao chân sứ"),
                new BuilderPort("ShedCount", GeaType.Integer, true, null, "Số tán"),
                L("TurretDiameter", "Đường kính chân sứ (0: 1.2 x Diameter)", false),
                L("TurretTaper", "Chiều cao phần vát côn ở đỉnh chân sứ (0: đỉnh phẳng)", false),
                L("CapHeight", "Chiều cao nắp đầu cực ở đỉnh sứ (0: không có)", false),
                new BuilderPort("SeparateTurret", GeaType.Bool, false, GeaValue.Boolean(false),
                    "TRUE: chân sứ là body riêng (đầu ra Turret), để tô màu khác thân sứ"),
            },
            new[] { BodyOut, new BuilderPort("Turret", GeaType.Reference, Description: "Chân sứ (cùng body với Body nếu không tách)") }),

        new BuilderManifest("Radiator", "1.0", "PART",
            "Cánh tản nhiệt dạng tấm: hộp góp trên và dưới, các lá tản nhiệt đứng song song chiều sâu, hai tấm bịt hai đầu. Một solid body.",
            new[]
            {
                L("CenterX", "Tâm theo X", false), L("CenterD", "Tâm theo chiều sâu", false), L("Elevation", "Cao độ mặt đáy", false),
                L("SizeX", "Bề rộng theo X"), L("SizeD", "Chiều sâu (vuông góc thành thùng)"), L("Height", "Chiều cao"),
                new BuilderPort("FinCount", GeaType.Integer, true, null, "Số lá tản nhiệt"),
                new BuilderPort("FinThickness", GeaType.Length, false, GeaValue.Length(12), "Độ dày lá"),
                new BuilderPort("HeaderHeight", GeaType.Length, false, GeaValue.Length(80), "Chiều cao hộp góp trên/dưới"),
                new BuilderPort("EndPlate", GeaType.Length, false, GeaValue.Length(25), "Độ dày tấm bịt hai đầu"),
            },
            new[] { BodyOut }),
    };

    public static BuilderManifest? Find(string name) => All.FirstOrDefault(m => m.Name == name);

    /// <summary>Đầu vào đầy đủ (thêm mặc định cho đầu vào tùy chọn bị bỏ qua).</summary>
    public static Dictionary<string, GeaValue> CompleteInputs(BuilderManifest m, IReadOnlyDictionary<string, GeaValue> given)
    {
        var result = new Dictionary<string, GeaValue>(given, StringComparer.Ordinal);
        foreach (var p in m.Inputs)
            if (!result.ContainsKey(p.Name) && p.Default != null)
                result[p.Name] = p.Default;
        return result;
    }
}
