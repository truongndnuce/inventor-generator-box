using System;
using System.Collections.Generic;
using Inv = Inventor;

namespace InventorAddin.Services.Gea;

/// <summary>
/// Các khối hình học cơ bản cho builder của .gea, dựng trong một Part (thừa hưởng từ TransformerBuilder cũ).
///
/// Hệ tọa độ: Inventor mặc định trục đứng là Y (mặt đất là mặt XZ). Dùng ba tọa độ (x, h, d):
/// x = dọc (X), h = cao độ (Y), d = chiều sâu (Z). Mọi vị trí đều đi qua <see cref="Model"/> và ModelToSketchSpace
/// nên không phụ thuộc chiều trục của sketch. Đơn vị vào là mm; API dùng cm.
///
/// Mọi sketch, feature, work plane tạo ra đều được báo qua <see cref="_track"/> để Host gắn dấu sở hữu,
/// nhờ đó lần Apply sau xóa đúng phần do script tạo rồi dựng lại.
/// </summary>
internal sealed class PartGeometry
{
    private const double Eps = 1e-6;

    private readonly Inv.PartComponentDefinition _def;
    private readonly Inv.TransientGeometry _tg;
    private readonly Inv.TransientObjects _to;
    private readonly Action<object> _track;
    private readonly Dictionary<(int axis, double offset), Inv.WorkPlane> _planes = new();

    public PartGeometry(Inv.Application app, Inv.PartDocument part, Action<object> track)
    {
        _def = part.ComponentDefinition;
        _tg = app.TransientGeometry;
        _to = app.TransientObjects;
        _track = track;
    }

    public static double Cm(double mm) => mm / 10.0;

    /// <summary>Điểm 3D (cm) từ tọa độ (x, h, d) tính bằng mm.</summary>
    public Inv.Point Model(double x, double h, double d) => _tg.CreatePoint(Cm(x), Cm(h), Cm(d));

    /// <summary>Kết quả của một thao tác dựng: body và các đối tượng đã tạo (để đổi tên theo body).</summary>
    public sealed class Built
    {
        public Built(Inv.SurfaceBody body) => Body = body;

        public Inv.SurfaceBody Body { get; set; }
        public List<(object Item, string Role)> Items { get; } = new();
    }

    // ------------------------------------------------------------ khối lăng trụ đứng

    /// <summary>Lăng trụ đứng (hình chữ nhật bo góc) thành body mới: tâm (cx, cd), đáy ở cao độ h0, cao <paramref name="height"/>.</summary>
    public Built Prism(string tag, double cx, double cd, double h0, double sx, double sd, double radius, double height)
    {
        var sk = NewSketch(PlaneAt(2, h0), tag + "_Sketch");
        RoundedRect(sk, cx, cd, h0, sx, sd, radius);
        var ext = Extrude(sk, height, up: true, Inv.PartFeatureOperationEnum.kNewBodyOperation, null, tag + "_Extrude");
        var built = new Built(ext.SurfaceBodies[1]);
        built.Items.Add((sk, "Sketch"));
        built.Items.Add((ext, "Extrude"));
        return built;
    }

    /// <summary>Khoét hốc từ mặt phẳng ngang ở cao độ <paramref name="hTop"/> xuống sâu <paramref name="depth"/>, chỉ trên body đích.</summary>
    public void Pocket(Built target, string tag, double cx, double cd, double hTop, double sx, double sd, double radius, double depth)
    {
        var sk = NewSketch(PlaneAt(2, hTop), tag + "_Sketch");
        RoundedRect(sk, cx, cd, hTop, sx, sd, radius);
        var ext = Extrude(sk, depth, up: false, Inv.PartFeatureOperationEnum.kCutOperation, target.Body, tag + "_Cut");
        target.Items.Add((sk, tag + "Sketch"));
        target.Items.Add((ext, tag));
        RefreshBody(target, ext);
    }

    /// <summary>
    /// Nối thêm một lăng trụ (có thể rỗng ở giữa) vào body đích: vẽ trên mặt phẳng ngang ở cao độ <paramref name="h0"/>,
    /// kéo lên (<paramref name="up"/>) hoặc xuống một đoạn <paramref name="height"/>.
    /// Nếu có kích thước lỗ (holeSx, holeSd &gt; 0) thì profile là vành giữa hai đường bao.
    /// </summary>
    public void JoinPrism(Built target, string tag, double cx, double cd, double h0, double sx, double sd, double radius,
        double height, bool up, double holeSx = 0, double holeSd = 0, double holeRadius = 0)
    {
        var sk = NewSketch(PlaneAt(2, h0), tag + "_Sketch");
        RoundedRect(sk, cx, cd, h0, sx, sd, radius);
        if (holeSx > Eps && holeSd > Eps) RoundedRect(sk, cx, cd, h0, holeSx, holeSd, holeRadius);
        var ext = Extrude(sk, height, up, Inv.PartFeatureOperationEnum.kJoinOperation, target.Body, tag + "_Extrude");
        target.Items.Add((sk, tag + "Sketch"));
        target.Items.Add((ext, tag));
        RefreshBody(target, ext);
    }

    // ------------------------------------------------------------ trụ

    /// <summary>
    /// Trụ tròn thành body mới. axis "D": trục theo chiều sâu, đối xứng quanh cd; "X": trục theo X, đối xứng quanh cx;
    /// "H": trục đứng, đáy ở h. Với trụ nằm ngang, h là cao độ của trục.
    /// </summary>
    public Built Cylinder(string tag, string axis, double cx, double cd, double h, double diameter, double length)
    {
        Inv.PlanarSketch sk;
        Inv.ExtrudeFeature ext;
        switch (axis)
        {
            case "H":
                sk = NewSketch(PlaneAt(2, h), tag + "_Sketch");
                sk.SketchCircles.AddByCenterRadius(sk.ModelToSketchSpace(Model(cx, h, cd)), Cm(diameter / 2));
                ext = Extrude(sk, length, up: true, Inv.PartFeatureOperationEnum.kNewBodyOperation, null, tag + "_Extrude");
                break;
            case "X":
                sk = NewSketch(PlaneAt(1, cx), tag + "_Sketch");
                sk.SketchCircles.AddByCenterRadius(sk.ModelToSketchSpace(Model(cx, h, cd)), Cm(diameter / 2));
                ext = ExtrudeSymmetric(sk, length, tag + "_Extrude");
                break;
            case "D":
                sk = NewSketch(PlaneAt(3, cd), tag + "_Sketch");
                sk.SketchCircles.AddByCenterRadius(sk.ModelToSketchSpace(Model(cx, h, cd)), Cm(diameter / 2));
                ext = ExtrudeSymmetric(sk, length, tag + "_Extrude");
                break;
            default:
                throw new ArgumentException($"Axis phải là \"D\", \"X\" hoặc \"H\" (nhận \"{axis}\").");
        }
        var built = new Built(ext.SurfaceBodies[1]);
        built.Items.Add((sk, "Sketch"));
        built.Items.Add((ext, "Extrude"));
        return built;
    }

    // ------------------------------------------------------------ sứ xuyên

    /// <summary>
    /// Sứ xuyên: một solid tròn xoay gồm chân sứ hình trụ và thân sứ có các tán (sheds) thu nhỏ dần lên trên.
    /// Hồ sơ vẽ trên mặt phẳng song song mặt trước (cách gốc d), trục quay là đường thẳng đứng đi qua tâm sứ.
    /// </summary>
    /// <remarks>
    /// Khi <paramref name="separateTurret"/> thì chân sứ là một body riêng (để tô màu kim loại khác thân sứ);
    /// khi không tách, <c>Turret</c> trỏ cùng body với <c>Porcelain</c>.
    /// </remarks>
    public (Built Porcelain, Built Turret) Bushing(string tag, double x, double d, double hBase, double dia, double length,
        double turretH, int sheds, double turretDia, double turretTaper, double capHeight, bool separateTurret)
    {
        if (sheds < 1) throw new ArgumentException("ShedCount phải lớn hơn 0.");
        double rTurret = turretDia > Eps ? turretDia / 2 : 0.6 * dia;
        double rCore = 0.2 * dia, rShed = 0.5 * dia;
        if (rTurret <= rCore + Eps) throw new ArgumentException("TurretDiameter phải lớn hơn 0.4 x Diameter (lõi sứ).");
        turretTaper = Math.Clamp(turretTaper, 0, 0.9 * turretH);
        double hTurretTop = hBase + turretH;
        double pitch = length / sheds;

        // Chân sứ: (r, h) theo X từ trục sứ
        var turret = new List<(double r, double h)> { (0, hBase), (rTurret, hBase) };
        if (turretTaper > Eps)
        {
            // Đỉnh chân sứ vát côn (giống vòm), thu về gần lõi sứ
            double rNeck = Math.Max(rCore * 1.4, rTurret * 0.4);
            turret.Add((rTurret, hTurretTop - turretTaper));
            turret.Add((rNeck, hTurretTop));
        }
        else turret.Add((rTurret, hTurretTop));

        List<(double r, double h)> pts;
        Built? turretBuilt = null;
        if (separateTurret)
        {
            turret.Add((0, hTurretTop));
            turretBuilt = Revolve(tag + "_Turret", x, d, turret);
            // Thân sứ lồng 1 mm vào chân sứ để hai body chạm nhau, không hở
            pts = new List<(double r, double h)> { (0, hTurretTop - 1), (rCore, hTurretTop - 1) };
        }
        else
        {
            pts = turret;
            pts.Add((rCore, hTurretTop));
        }
        for (int i = 0; i < sheds; i++)
        {
            double h = hTurretTop + i * pitch;
            double tip = rShed * (1 - (sheds > 1 ? 0.3 * i / (sheds - 1) : 0));
            pts.Add((rCore, h + 0.15 * pitch));   // mặt dưới tán gặp lõi
            pts.Add((tip, h + 0.15 * pitch));     // mặt dưới tán đi ra mép
            pts.Add((tip, h + 0.25 * pitch));     // dày mép tán
            pts.Add((rCore, h + 0.60 * pitch));   // mặt trên tán dốc vào lõi
        }
        double hTop = hTurretTop + length;
        pts.Add((rCore, hTop));
        if (capHeight > Eps)
        {
            // Nắp đầu cực: một đĩa rộng hơn lõi, vát nhọn lên tâm
            double rCap = 0.32 * dia;
            pts.Add((rCap, hTop));
            pts.Add((rCap, hTop + capHeight * 0.5));
            pts.Add((rCore * 0.5, hTop + capHeight));
            pts.Add((0, hTop + capHeight));
        }
        else pts.Add((0, hTop));

        var porcelain = Revolve(tag, x, d, pts);
        return (porcelain, turretBuilt ?? porcelain);
    }

    /// <summary>
    /// Solid tròn xoay quanh trục đứng qua (x, d): hồ sơ là đường gấp khúc (r, h) bắt đầu và kết thúc trên trục (r = 0);
    /// cạnh khép kín chạy dọc trục và là trục quay. Vẽ trên mặt phẳng song song mặt trước ở chiều sâu d.
    /// </summary>
    private Built Revolve(string tag, double x, double d, List<(double r, double h)> pts)
    {
        var sk = NewSketch(PlaneAt(3, d), tag + "_Sketch");
        Inv.Point2d Pt(double r, double h) => sk.ModelToSketchSpace(Model(x + r, h, d));

        Inv.SketchLine? first = null, prev = null;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            prev = prev == null
                ? sk.SketchLines.AddByTwoPoints(Pt(pts[i].r, pts[i].h), Pt(pts[i + 1].r, pts[i + 1].h))
                : sk.SketchLines.AddByTwoPoints(prev.EndSketchPoint, Pt(pts[i + 1].r, pts[i + 1].h));
            first ??= prev;
        }
        var axis = sk.SketchLines.AddByTwoPoints(prev!.EndSketchPoint, first!.StartSketchPoint);

        var rev = _def.Features.RevolveFeatures.AddFull(sk.Profiles.AddForSolid(), axis, Inv.PartFeatureOperationEnum.kNewBodyOperation);
        Track(rev, tag + "_Revolve");
        var built = new Built(rev.SurfaceBodies[1]);
        built.Items.Add((sk, "Sketch"));
        built.Items.Add((rev, "Revolve"));
        return built;
    }

    // ------------------------------------------------------------ cánh tản nhiệt

    /// <summary>
    /// Cánh tản nhiệt dạng tấm: hộp góp dưới (body mới), các lá đứng và hai tấm bịt đầu (một sketch nhiều vòng, nối vào),
    /// hộp góp trên (nối). Lá dày theo X, trải hết chiều sâu (trừ một chút cho mép hộp góp), chia đều giữa hai tấm bịt.
    /// </summary>
    public Built Radiator(string tag, double cx, double cd, double h0, double sx, double sd, double height,
        int fins, double finT, double headerH, double endT)
    {
        // Hộp góp hẹp hơn lá theo chiều sâu, nằm giữa
        double headerSd = Math.Max(sd * 0.35, Math.Min(sd, 2 * headerH));
        var built = Prism(tag, cx, cd, h0, sx, headerSd, 0, headerH);

        double hFin = h0 + headerH, finH = height - 2 * headerH;
        var sk = NewSketch(PlaneAt(2, hFin), tag + "_Fins_Sketch");
        Inv.Point2d S(double x, double d) => sk.ModelToSketchSpace(Model(x, hFin, d));
        void Rect(double x1, double x2, double d1, double d2) => sk.SketchLines.AddAsTwoPointRectangle(S(x1, d1), S(x2, d2));

        double xL = cx - sx / 2, xR = cx + sx / 2, dF = cd - sd / 2, dB = cd + sd / 2;
        Rect(xL, xL + endT, dF, dB);
        Rect(xR - endT, xR, dF, dB);
        double inner = sx - 2 * endT, pitch = inner / fins;
        double finSd = sd * 0.94;
        for (int i = 0; i < fins; i++)
        {
            double xc = xL + endT + pitch * (i + 0.5);
            Rect(xc - finT / 2, xc + finT / 2, cd - finSd / 2, cd + finSd / 2);
        }
        var finsExt = Extrude(sk, finH, up: true, Inv.PartFeatureOperationEnum.kJoinOperation, built.Body, tag + "_Fins");
        built.Items.Add((sk, "FinsSketch"));
        built.Items.Add((finsExt, "Fins"));
        RefreshBody(built, finsExt);

        JoinPrism(built, tag + "_TopHeader", cx, cd, hFin + finH, sx, headerSd, 0, headerH, up: true);
        return built;
    }

    // ------------------------------------------------------------ tiện ích

    /// <summary>
    /// Hình chữ nhật bo góc trên sketch nằm ngang ở cao độ h: 4 đoạn thẳng nối 4 cung (cung qua 3 điểm nên không phụ thuộc
    /// chiều của sketch). Bán kính 0 thì là hình chữ nhật thường.
    /// </summary>
    private void RoundedRect(Inv.PlanarSketch sk, double cx, double cd, double h, double sx, double sd, double r)
    {
        if (sx <= Eps || sd <= Eps) throw new ArgumentException($"Kích thước hình chữ nhật phải dương ({sx} x {sd} mm).");
        Inv.Point2d S(double x, double d) => sk.ModelToSketchSpace(Model(x, h, d));
        double x1 = cx - sx / 2, x2 = cx + sx / 2, d1 = cd - sd / 2, d2 = cd + sd / 2;

        // Bán kính bằng nửa cạnh làm đoạn thẳng dài 0: giữ một đoạn tối thiểu
        r = Math.Min(Math.Max(r, 0), Math.Min(sx, sd) / 2 - 0.01);
        if (r <= 0.001)
        {
            sk.SketchLines.AddAsTwoPointRectangle(S(x1, d1), S(x2, d2));
            return;
        }

        var lines = sk.SketchLines;
        var bottom = lines.AddByTwoPoints(S(x1 + r, d1), S(x2 - r, d1));
        var right = lines.AddByTwoPoints(S(x2, d1 + r), S(x2, d2 - r));
        var top = lines.AddByTwoPoints(S(x2 - r, d2), S(x1 + r, d2));
        var left = lines.AddByTwoPoints(S(x1, d2 - r), S(x1, d1 + r));

        double k = r * (1 - 1 / Math.Sqrt(2)); // từ góc vuông tới điểm giữa cung
        var arcs = sk.SketchArcs;
        arcs.AddByThreePoints(bottom.EndSketchPoint, S(x2 - k, d1 + k), right.StartSketchPoint);
        arcs.AddByThreePoints(right.EndSketchPoint, S(x2 - k, d2 - k), top.StartSketchPoint);
        arcs.AddByThreePoints(top.EndSketchPoint, S(x1 + k, d2 - k), left.StartSketchPoint);
        arcs.AddByThreePoints(left.EndSketchPoint, S(x1 + k, d1 + k), bottom.StartSketchPoint);
    }

    private Inv.PlanarSketch NewSketch(Inv.WorkPlane plane, string name)
    {
        var sk = _def.Sketches.Add(plane);
        Track(sk, name);
        return sk;
    }

    private Inv.ExtrudeFeature Extrude(Inv.PlanarSketch sk, double distance, bool up, Inv.PartFeatureOperationEnum op,
        Inv.SurfaceBody? affected, string name)
    {
        var ed = _def.Features.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), op);
        // Pháp tuyến mặt phẳng có thể hướng xuống: chọn chiều theo pháp tuyến thật để "lên" là +Y.
        var normalUp = ((Inv.Plane)((Inv.WorkPlane)sk.PlanarEntity).Plane).Normal.Y >= 0;
        var dir = normalUp == up
            ? Inv.PartFeatureExtentDirectionEnum.kPositiveExtentDirection
            : Inv.PartFeatureExtentDirectionEnum.kNegativeExtentDirection;
        ed.SetDistanceExtent(Cm(distance), dir);
        if (affected != null)
        {
            var bodies = _to.CreateObjectCollection();
            bodies.Add(affected);
            ed.AffectedBodies = bodies;
        }
        var ext = _def.Features.ExtrudeFeatures.Add(ed);
        Track(ext, name);
        return ext;
    }

    private Inv.ExtrudeFeature ExtrudeSymmetric(Inv.PlanarSketch sk, double distance, string name)
    {
        var ed = _def.Features.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), Inv.PartFeatureOperationEnum.kNewBodyOperation);
        ed.SetDistanceExtent(Cm(distance), Inv.PartFeatureExtentDirectionEnum.kSymmetricExtentDirection);
        var ext = _def.Features.ExtrudeFeatures.Add(ed);
        Track(ext, name);
        return ext;
    }

    /// <summary>Sau khi nối hoặc khoét, body đích có thể là đối tượng mới: lấy lại từ feature.</summary>
    private static void RefreshBody(Built target, Inv.ExtrudeFeature ext)
    {
        try
        {
            if (ext.SurfaceBodies.Count > 0) target.Body = ext.SurfaceBodies[1];
        }
        catch { /* giữ body cũ */ }
    }

    private void Track(object item, string name)
    {
        try
        {
            switch (item)
            {
                case Inv.PlanarSketch s: s.Name = name; break;
                case Inv.PartFeature f: f.Name = name; break;
                case Inv.WorkPlane w: w.Name = name; break;
            }
        }
        catch { /* trùng tên: để tên do Inventor đặt */ }
        _track(item);
    }

    /// <summary>
    /// Mặt phẳng song song một mặt phẳng gốc (axis: 1 = YZ ở x, 2 = mặt đất XZ ở cao độ h, 3 = mặt trước XY ở chiều sâu d)
    /// và nằm đúng ở <paramref name="value"/> mm. Chiều dương của offset phụ thuộc pháp tuyến nên kiểm tra bằng hình học thật
    /// và đảo dấu nếu lệch.
    /// </summary>
    public Inv.WorkPlane PlaneAt(int axis, double value)
    {
        var basePlane = _def.WorkPlanes[axis];
        if (Math.Abs(value) < Eps) return basePlane;
        var key = (axis, Math.Round(value, 6));
        if (_planes.TryGetValue(key, out var existing)) return existing;

        var plane = _def.WorkPlanes.AddByPlaneAndOffset(basePlane, Cm(value), false);
        double actual = Coord(plane, axis);
        if (Math.Abs(actual - Cm(value)) > 1e-4)
        {
            plane.Delete();
            plane = _def.WorkPlanes.AddByPlaneAndOffset(basePlane, -Cm(value), false);
            actual = Coord(plane, axis);
            if (Math.Abs(actual - Cm(value)) > 1e-4)
                throw new InvalidOperationException($"Could not place a work plane at {value} mm on axis {axis} (got {actual * 10} mm).");
        }
        plane.Visible = false;
        Track(plane, $"GEA_Plane_{"XHD"[axis - 1]}_{value:0.###}".Replace('.', '_').Replace('-', 'm'));
        _planes[key] = plane;
        return plane;
    }

    /// <summary>Tọa độ của mặt phẳng dọc theo trục pháp tuyến của nó (X, Y hoặc Z).</summary>
    private static double Coord(Inv.WorkPlane plane, int axis)
    {
        var p = ((Inv.Plane)plane.Plane).RootPoint;
        return axis switch { 1 => p.X, 2 => p.Y, _ => p.Z };
    }
}
