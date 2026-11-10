using System;
using System.Collections.Generic;
using Inv = Inventor;

namespace InventorAddin.Services.Gea;

/// <summary>
/// Basic geometry blocks for the .gea builders, built in a Part (inherited from the old TransformerBuilder).
///
/// Coordinate system: in Inventor the vertical axis is Y by default (the ground is the XZ plane). Three coordinates (x, h, d) are used:
/// x = along (X), h = elevation (Y), d = depth (Z). Every position goes through <see cref="Model"/> and ModelToSketchSpace
/// so it does not depend on the axis direction of the sketch. Input units are mm; the API uses cm.
///
/// Every sketch, feature and work plane created is reported through <see cref="_track"/> so the Host can attach the ownership mark,
/// so the next Apply deletes exactly what the script created and rebuilds it.
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

    /// <summary>A 3D point (cm) from coordinates (x, h, d) in mm.</summary>
    public Inv.Point Model(double x, double h, double d) => _tg.CreatePoint(Cm(x), Cm(h), Cm(d));

    /// <summary>Result of one build operation: the body and the objects created (so they can be renamed after the body).</summary>
    public sealed class Built
    {
        public Built(Inv.SurfaceBody body) => Body = body;

        public Inv.SurfaceBody Body { get; set; }
        public List<(object Item, string Role)> Items { get; } = new();
    }

    // ------------------------------------------------------------ vertical prisms

    /// <summary>A vertical prism (rectangle with rounded corners) as a new body: center (cx, cd), bottom at elevation h0, height <paramref name="height"/>.</summary>
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

    /// <summary>Cuts a pocket from a horizontal plane at elevation <paramref name="hTop"/> down by <paramref name="depth"/>, on the target body only.</summary>
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
    /// Joins a prism (it may be hollow in the middle) to the target body: sketched on a horizontal plane at elevation <paramref name="h0"/>,
    /// extruded up (<paramref name="up"/>) or down by <paramref name="height"/>.
    /// If hole dimensions are given (holeSx, holeSd &gt; 0) the profile is the ring between the two outlines.
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

    // ------------------------------------------------------------ cylinders

    /// <summary>
    /// A round cylinder as a new body. axis "D": axis along the depth, symmetric about cd; "X": axis along X, symmetric about cx;
    /// "H": vertical axis, bottom at h. For a horizontal cylinder, h is the elevation of the axis.
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
                throw new ArgumentException($"Axis must be \"D\", \"X\" or \"H\" (got \"{axis}\").");
        }
        var built = new Built(ext.SurfaceBodies[1]);
        built.Items.Add((sk, "Sketch"));
        built.Items.Add((ext, "Extrude"));
        return built;
    }

    // ------------------------------------------------------------ bushings

    /// <summary>
    /// Bushing: one solid of revolution made of a cylindrical turret and an insulator body whose sheds shrink going up.
    /// The profile is drawn on a plane parallel to the front plane (at distance d from the origin); the axis of revolution is the vertical line through the bushing center.
    /// </summary>
    /// <remarks>
    /// When <paramref name="separateTurret"/> is set, the turret is a separate body (so it can get a different metal color from the insulator);
    /// when not separated, <c>Turret</c> points to the same body as <c>Porcelain</c>.
    /// </remarks>
    public (Built Porcelain, Built Turret) Bushing(string tag, double x, double d, double hBase, double dia, double length,
        double turretH, int sheds, double turretDia, double turretTaper, double capHeight, bool separateTurret)
    {
        if (sheds < 1) throw new ArgumentException("ShedCount must be greater than 0.");
        double rTurret = turretDia > Eps ? turretDia / 2 : 0.6 * dia;
        double rCore = 0.2 * dia, rShed = 0.5 * dia;
        if (rTurret <= rCore + Eps) throw new ArgumentException("TurretDiameter must be greater than 0.4 x Diameter (the porcelain core).");
        turretTaper = Math.Clamp(turretTaper, 0, 0.9 * turretH);
        double hTurretTop = hBase + turretH;
        double pitch = length / sheds;

        // Turret: (r, h) along X from the bushing axis
        var turret = new List<(double r, double h)> { (0, hBase), (rTurret, hBase) };
        if (turretTaper > Eps)
        {
            // The top of the turret is tapered (like a dome), narrowing toward the porcelain core
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
            // The insulator body is sunk 1 mm into the turret so the two bodies touch, with no gap
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
            pts.Add((rCore, h + 0.15 * pitch));   // underside of the shed meets the core
            pts.Add((tip, h + 0.15 * pitch));     // underside of the shed goes out to the edge
            pts.Add((tip, h + 0.25 * pitch));     // thickness of the shed edge
            pts.Add((rCore, h + 0.60 * pitch));   // upper side of the shed slopes back to the core
        }
        double hTop = hTurretTop + length;
        pts.Add((rCore, hTop));
        if (capHeight > Eps)
        {
            // Terminal cap: a disc wider than the core, tapering up to the center
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
    /// A solid of revolution about the vertical axis through (x, d): the profile is a polyline (r, h) that starts and ends on the axis (r = 0);
    /// the closing edge runs along the axis and is the axis of revolution. Drawn on a plane parallel to the front plane at depth d.
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

    // ------------------------------------------------------------ radiators

    /// <summary>
    /// Plate-type radiator: the lower header (new body), the vertical fins and two end plates (one sketch with several loops, joined),
    /// the upper header (joined). The fins are thick along X, span the whole depth (less a little for the header edge), and are spaced evenly between the two end plates.
    /// </summary>
    public Built Radiator(string tag, double cx, double cd, double h0, double sx, double sd, double height,
        int fins, double finT, double headerH, double endT)
    {
        // The header is narrower than the fins along the depth, sitting in the middle
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

    // ------------------------------------------------------------ utilities

    /// <summary>
    /// A rounded rectangle on a horizontal sketch at elevation h: 4 straight segments joined by 4 arcs (arcs through 3 points so they do not depend on
    /// the direction of the sketch). A radius of 0 gives a plain rectangle.
    /// </summary>
    private void RoundedRect(Inv.PlanarSketch sk, double cx, double cd, double h, double sx, double sd, double r)
    {
        if (sx <= Eps || sd <= Eps) throw new ArgumentException($"The rectangle size must be positive ({sx} x {sd} mm).");
        Inv.Point2d S(double x, double d) => sk.ModelToSketchSpace(Model(x, h, d));
        double x1 = cx - sx / 2, x2 = cx + sx / 2, d1 = cd - sd / 2, d2 = cd + sd / 2;

        // A radius equal to half the side makes a straight segment of length 0: keep a minimum segment
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

        double k = r * (1 - 1 / Math.Sqrt(2)); // from the right-angle corner to the middle of the arc
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
        // The plane normal may point down: choose the direction from the real normal so that "up" is +Y.
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

    /// <summary>After joining or cutting, the target body may be a new object: take it again from the feature.</summary>
    private static void RefreshBody(Built target, Inv.ExtrudeFeature ext)
    {
        try
        {
            if (ext.SurfaceBodies.Count > 0) target.Body = ext.SurfaceBodies[1];
        }
        catch { /* keep the old body */ }
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
        catch { /* duplicate name: keep the name Inventor chose */ }
        _track(item);
    }

    /// <summary>
    /// A plane parallel to a base plane (axis: 1 = YZ at x, 2 = ground XZ at elevation h, 3 = front XY at depth d)
    /// and located exactly at <paramref name="value"/> mm. The positive direction of the offset depends on the normal, so it is checked with the real geometry
    /// and the sign is flipped if it is off.
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

    /// <summary>Coordinate of the plane along its normal axis (X, Y or Z).</summary>
    private static double Coord(Inv.WorkPlane plane, int axis)
    {
        var p = ((Inv.Plane)plane.Plane).RootPoint;
        return axis switch { 1 => p.X, 2 => p.Y, _ => p.Z };
    }
}
