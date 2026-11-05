using System;
using System.Collections.Generic;
using InventorAddin.Models;
using Inv = Inventor;

namespace InventorAddin.Services;

/// <summary>
/// Dựng máy biến áp lực đơn giản hóa trong một Part: mỗi bộ phận là một solid body có tên (TR_Base, TR_Tank, TR_Cover,
/// TR_Radiator_n, TR_Conservator, TR_Bushing_n...). Hình khối theo ảnh tham chiếu, bỏ qua chi tiết nhỏ.
///
/// Hệ tọa độ: Inventor mặc định trục đứng là Y (mặt đất là mặt XZ). Trong code dùng ba tọa độ (x, h, d):
/// x = dọc thùng (X), h = chiều cao (Y), d = chiều sâu (Z). Đế nằm trên h = 0, tâm thùng ở x = d = 0,
/// cánh tản nhiệt ở phía d dương, bình dầu ở đầu x âm. Mọi vị trí đều đi qua <see cref="Model"/> và ModelToSketchSpace
/// nên không phụ thuộc chiều trục của sketch.
///
/// Lưu ý: hình học được dựng từ giá trị đã tính sẵn (không gắn User Parameter), vì vị trí sứ và cánh tản nhiệt phụ thuộc số lượng.
/// Phải chạy bên trong một Transaction do gateway quản lý. Đơn vị API là cm, mọi giá trị mm đổi qua <see cref="Cm"/>.
/// </summary>
internal sealed class TransformerBuilder
{
    private const double Eps = 1e-6;

    private readonly Inv.Application _app;
    private readonly Inv.PartDocument _part;
    private readonly Inv.PartComponentDefinition _def;
    private readonly IReadOnlyDictionary<string, double> _v;
    private readonly Inv.TransientGeometry _tg;
    private readonly Dictionary<(int axis, double offset), Inv.WorkPlane> _planes = new();

    public TransformerBuilder(Inv.Application app, Inv.PartDocument part, IReadOnlyDictionary<string, double> values)
    {
        _app = app;
        _part = part;
        _def = part.ComponentDefinition;
        _v = values;
        _tg = app.TransientGeometry;
    }

    private static double Cm(double mm) => mm / 10.0;

    private double P(string name) => _v[name];

    // Các mặt phẳng gốc của Part: 1 = YZ, 2 = XZ (mặt đất), 3 = XY (mặt trước)
    private Inv.WorkPlane Ground => _def.WorkPlanes[2];
    private Inv.WorkPlane Front => _def.WorkPlanes[3];

    /// <summary>Điểm 3D (cm) từ tọa độ (x, h, d) tính bằng mm.</summary>
    private Inv.Point Model(double x, double h, double d) => _tg.CreatePoint(Cm(x), Cm(h), Cm(d));

    public void Build()
    {
        double len = P(TransformerParams.TankLength), wid = P(TransformerParams.TankWidth), hgt = P(TransformerParams.TankHeight);
        double baseT = P(TransformerParams.BaseThickness), baseO = P(TransformerParams.BaseOverhang);
        double covT = P(TransformerParams.CoverThickness), covO = P(TransformerParams.CoverOverhang);

        double hTank = baseT;
        double hCover = baseT + hgt;
        double hCoverTop = hCover + covT;

        Box("TR_Base", 0, 0, 0, len + 2 * baseO, wid + 2 * baseO, baseT);
        Box("TR_Tank", 0, 0, hTank, len, wid, hgt);
        Box("TR_Cover", 0, 0, hCover, len + 2 * covO, wid + 2 * covO, covT);

        BuildRadiators(wid, hTank);
        BuildConservator(len, hCoverTop);
        BuildBushings(len, wid, hCoverTop);
        ApplyAppearances();
    }

    // ------------------------------------------------------------ Màu sắc (chỉ thẩm mỹ, lỗi thì bỏ qua)

    private const string GreyAssetId = "Plastic-010";        // "Smooth - Light Gray"
    private const string PorcelainAssetId = "Fabric-006"; // "Dark Brown": nâu sẫm như sứ trong ảnh

    private void ApplyAppearances()
    {
        try
        {
            var grey = DocumentAsset(GreyAssetId);
            var brown = DocumentAsset(PorcelainAssetId);
            foreach (Inv.SurfaceBody body in _def.SurfaceBodies)
            {
                var asset = body.Name.StartsWith("TR_Bushing_", StringComparison.Ordinal) ? brown : grey;
                if (asset != null) body.Appearance = asset;
            }
        }
        catch
        {
            // Màu không ảnh hưởng hình học; thư viện vật liệu thiếu hoặc khác phiên bản thì để màu mặc định.
        }
    }

    /// <summary>Lấy asset theo mã từ các thư viện của Inventor và sao chép vào tài liệu (body chỉ gán được asset có trong tài liệu).</summary>
    private Inv.Asset? DocumentAsset(string id)
    {
        foreach (Inv.AssetLibrary lib in _app.AssetLibraries)
        {
            Inv.Asset? found = null;
            try { found = lib.AppearanceAssets[id]; } catch { /* thư viện này không có */ }
            if (found != null)
                return found.CopyTo((Inv._Document)_part, true);
        }
        return null;
    }

    // ------------------------------------------------------------ Cánh tản nhiệt (phía d dương)

    private void BuildRadiators(double wid, double hTank)
    {
        int n = (int)Math.Round(P(TransformerParams.RadiatorCount));
        if (n == 0) return;
        double w = P(TransformerParams.RadiatorWidth), depth = P(TransformerParams.RadiatorDepth);
        double h = P(TransformerParams.RadiatorHeight), gap = P(TransformerParams.RadiatorGap);

        double span = n * w + (n - 1) * gap;
        double x0 = -span / 2 + w / 2;
        double cd = wid / 2 + TransformerParams.RadiatorClearanceFromTank + depth / 2;
        for (int i = 0; i < n; i++)
            Box($"TR_Radiator_{i + 1}", x0 + i * (w + gap), cd, hTank + TransformerParams.RadiatorBottomOffset, w, depth, h);
    }

    // ------------------------------------------------------------ Bình dầu phụ (trụ nằm ngang theo d, đầu x âm, trên hai chân đỡ)

    private void BuildConservator(double len, double hCoverTop)
    {
        double dia = P(TransformerParams.ConservatorDiameter), l = P(TransformerParams.ConservatorLength);
        double lift = P(TransformerParams.ConservatorLift);
        double cx = -len / 2 + dia / 2;
        double ch = hCoverTop + lift + dia / 2;

        // Trụ: vòng tròn trên mặt trước (d = 0), kéo đối xứng hai phía theo d.
        var sk = _def.Sketches.Add(Front);
        sk.Name = "TR_ConservatorSketch";
        sk.SketchCircles.AddByCenterRadius(sk.ModelToSketchSpace(Model(cx, ch, 0)), Cm(dia / 2));
        var ed = _def.Features.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), Inv.PartFeatureOperationEnum.kNewBodyOperation);
        ed.SetDistanceExtent(Cm(l), Inv.PartFeatureExtentDirectionEnum.kSymmetricExtentDirection);
        var ext = _def.Features.ExtrudeFeatures.Add(ed);
        ext.Name = "TR_ConservatorExtrude";
        ext.SurfaceBodies[1].Name = "TR_Conservator";

        // Hai chân đỡ
        double legD = l * 0.3;
        Box("TR_ConservatorLeg_1", cx, -legD, hCoverTop, 100, 100, lift);
        Box("TR_ConservatorLeg_2", cx, legD, hCoverTop, 100, 100, lift);
    }

    // ------------------------------------------------------------ Sứ xuyên (hai hàng trên nắp)

    private void BuildBushings(double len, double wid, double hCoverTop)
    {
        int count = (int)Math.Round(P(TransformerParams.BushingCount));
        double dia = P(TransformerParams.BushingDiameter);
        double rt = TransformerParams.TurretRadius(dia);
        int rows = TransformerParams.BushingRows(count);
        int perRow = TransformerParams.BushingsPerRow(count);

        double xs = -len / 2 + P(TransformerParams.ConservatorDiameter) + rt + TransformerParams.BushingEdgeMargin;
        double xe = len / 2 - rt - TransformerParams.BushingEdgeMargin;
        double rowD = TransformerParams.BushingRowFraction * wid;

        int index = 0;
        for (int row = 0; row < rows && index < count; row++)
        {
            double d = rows == 1 ? 0 : (row == 0 ? -rowD : rowD);
            for (int i = 0; i < perRow && index < count; i++, index++)
            {
                double x = perRow == 1 ? (xs + xe) / 2 : xs + i * (xe - xs) / (perRow - 1);
                Bushing(index + 1, x, d, hCoverTop);
            }
        }
    }

    /// <summary>
    /// Một sứ xuyên là một solid tròn xoay: chân sứ hình trụ, thân sứ có các tán (sheds) thu nhỏ dần lên trên.
    /// Hồ sơ vẽ trên mặt phẳng song song mặt trước (cách gốc <paramref name="d"/>), trục quay là đường thẳng đứng đi qua tâm sứ.
    /// </summary>
    private void Bushing(int number, double x, double d, double hBase)
    {
        double dia = P(TransformerParams.BushingDiameter), length = P(TransformerParams.BushingLength);
        double turretH = P(TransformerParams.TurretHeight);
        int sheds = (int)Math.Round(P(TransformerParams.BushingShedCount));

        double rTurret = TransformerParams.TurretRadius(dia);
        double rCore = 0.2 * dia, rShed = 0.5 * dia;
        double hTurretTop = hBase + turretH;
        double pitch = length / sheds;

        var sk = _def.Sketches.Add(PlaneAt(axis: 3, d));
        sk.Name = $"TR_BushingSketch_{number}";

        // Điểm (r, h) -> điểm sketch; r đo theo X từ trục sứ.
        Inv.Point2d Pt(double r, double h) => sk.ModelToSketchSpace(Model(x + r, h, 0));

        var pts = new List<(double r, double h)>
        {
            (0, hBase), (rTurret, hBase), (rTurret, hTurretTop), (rCore, hTurretTop),
        };
        for (int i = 0; i < sheds; i++)
        {
            double h = hTurretTop + i * pitch;
            double tip = rShed * (1 - 0.3 * i / (sheds - 1));
            pts.Add((rCore, h + 0.15 * pitch));   // mặt dưới tán gặp lõi
            pts.Add((tip, h + 0.15 * pitch));     // mặt dưới tán đi ra mép
            pts.Add((tip, h + 0.25 * pitch));     // dày mép tán
            pts.Add((rCore, h + 0.60 * pitch));   // mặt trên tán dốc vào lõi
        }
        double hTop = hTurretTop + length;
        pts.Add((rCore, hTop));
        pts.Add((0, hTop));

        // Vòng kín: nối tiếp các cạnh; cạnh cuối chạy dọc trục từ đỉnh về chân sứ và là trục quay.
        Inv.SketchLine? prev = null;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            prev = prev == null
                ? sk.SketchLines.AddByTwoPoints(Pt(pts[i].r, pts[i].h), Pt(pts[i + 1].r, pts[i + 1].h))
                : sk.SketchLines.AddByTwoPoints(prev.EndSketchPoint, Pt(pts[i + 1].r, pts[i + 1].h));
        }
        var axis = sk.SketchLines.AddByTwoPoints(prev!.EndSketchPoint, sk.SketchLines[1].StartSketchPoint);

        var rev = _def.Features.RevolveFeatures.AddFull(sk.Profiles.AddForSolid(), axis, Inv.PartFeatureOperationEnum.kNewBodyOperation);
        rev.Name = $"TR_BushingRevolve_{number}";
        rev.SurfaceBodies[1].Name = $"TR_Bushing_{number}";
    }

    // ------------------------------------------------------------ Tiện ích

    /// <summary>
    /// Khối hộp thành một body mới: tâm (cx, cd) trên mặt phẳng ngang cao h0, kích thước sx (theo x) × sd (theo d) × sh (cao), mm.
    /// Sketch vẽ trên mặt đất (hoặc mặt phẳng song song), hai góc chéo đi qua ModelToSketchSpace; chiều kéo lấy theo pháp tuyến thật của mặt phẳng.
    /// </summary>
    private Inv.SurfaceBody Box(string name, double cx, double cd, double h0, double sx, double sd, double sh)
    {
        var plane = PlaneAt(axis: 2, h0);
        var sk = _def.Sketches.Add(plane);
        sk.Name = name + "_Sketch";
        sk.SketchLines.AddAsTwoPointRectangle(
            sk.ModelToSketchSpace(Model(cx - sx / 2, h0, cd - sd / 2)),
            sk.ModelToSketchSpace(Model(cx + sx / 2, h0, cd + sd / 2)));

        var ed = _def.Features.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), Inv.PartFeatureOperationEnum.kNewBodyOperation);
        // Kéo về phía +Y (lên trên): nếu pháp tuyến mặt phẳng hướng xuống thì kéo theo chiều ngược lại.
        var up = ((Inv.Plane)plane.Plane).Normal.Y >= 0
            ? Inv.PartFeatureExtentDirectionEnum.kPositiveExtentDirection
            : Inv.PartFeatureExtentDirectionEnum.kNegativeExtentDirection;
        ed.SetDistanceExtent(Cm(sh), up);
        var ext = _def.Features.ExtrudeFeatures.Add(ed);
        ext.Name = name + "_Extrude";
        var body = ext.SurfaceBodies[1];
        body.Name = name;
        return body;
    }

    /// <summary>
    /// Mặt phẳng song song một mặt phẳng gốc (axis: 2 = mặt đất XZ, cần cao độ h theo Y; 3 = mặt trước XY, cần chiều sâu d theo Z)
    /// và nằm đúng ở giá trị <paramref name="value"/> mm. Chiều dương của offset phụ thuộc pháp tuyến nên kiểm tra lại
    /// bằng hình học thật và đảo dấu nếu lệch.
    /// </summary>
    private Inv.WorkPlane PlaneAt(int axis, double value)
    {
        var basePlane = _def.WorkPlanes[axis];
        if (Math.Abs(value) < Eps) return basePlane;
        if (_planes.TryGetValue((axis, value), out var existing)) return existing;

        var plane = _def.WorkPlanes.AddByPlaneAndOffset(basePlane, Cm(value), false);
        double actual = Coord(plane, axis);
        if (Math.Abs(actual - Cm(value)) > 1e-4)
        {
            plane.Delete();
            plane = _def.WorkPlanes.AddByPlaneAndOffset(basePlane, -Cm(value), false);
            actual = Coord(plane, axis);
            // Hàng sứ đối xứng nên lệch dấu theo trục sâu không làm hỏng bố cục, nhưng cao độ thì phải đúng.
            if (axis == 2 && Math.Abs(actual - Cm(value)) > 1e-4)
                throw new InvalidOperationException($"Could not place a work plane at height {value} mm (got {actual * 10} mm).");
        }
        plane.Visible = false;
        _planes[(axis, value)] = plane;
        return plane;
    }

    /// <summary>Tọa độ của mặt phẳng dọc theo trục pháp tuyến của nó (Y cho mặt đất, Z cho mặt trước).</summary>
    private static double Coord(Inv.WorkPlane plane, int axis)
    {
        var p = ((Inv.Plane)plane.Plane).RootPoint;
        return axis == 2 ? p.Y : p.Z;
    }
}
