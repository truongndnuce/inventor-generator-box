using System;
using System.Collections.Generic;
using System.Linq;
using InventorAddin.Models;
using Inv = Inventor;

namespace InventorAddin.Services;

/// <summary>
/// Builds a parametric box: every dimension is bound to a BX_* User Parameter. The body and the lid are two solid bodies in the same Part.
/// The lid sits on the top face of the body (an offset plane BX_Height), and the lid lip points down into the box.
/// Must run inside a Transaction managed by the gateway.
/// </summary>
internal sealed class BoxBuilder
{
    private const double Tol = 1e-6;

    private readonly Inv.Application _app;
    private readonly Inv.PartDocument _part;
    private readonly Inv.PartComponentDefinition _def;
    private readonly IReadOnlyDictionary<string, double> _v;
    private readonly Inv.TransientGeometry _tg;

    public BoxBuilder(Inv.Application app, Inv.PartDocument part, IReadOnlyDictionary<string, double> values)
    {
        _app = app;
        _part = part;
        _def = part.ComponentDefinition;
        _v = values;
        _tg = app.TransientGeometry;
    }

    private double Cm(string name) => InventorBoxGateway.MmToCm(_v[name]);

    public void Build()
    {
        CreateParameters();
        BuildBody();
        BuildLid();
    }

    // ------------------------------------------------------------ Parameter

    private void CreateParameters()
    {
        var pars = _def.Parameters.UserParameters;
        foreach (var d in BoxParams.All)
            pars.AddByValue(d.Name, Cm(d.Name), Inv.UnitsTypeEnum.kMillimeterLengthUnits);
    }

    // ------------------------------------------------------------ Body

    private void BuildBody()
    {
        var xy = _def.WorkPlanes[3]; // XY
        var sk = _def.Sketches.Add(xy);
        sk.Name = "BX_BodySketch";
        CenteredRectangle(sk, BoxParams.Length, BoxParams.Width, Cm(BoxParams.Length), Cm(BoxParams.Width));

        var ed = _def.Features.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), Inv.PartFeatureOperationEnum.kNewBodyOperation);
        ed.SetDistanceExtent("BX_Height", Inv.PartFeatureExtentDirectionEnum.kPositiveExtentDirection);
        var ext = _def.Features.ExtrudeFeatures.Add(ed);
        ext.Name = "BX_BodyExtrude";
        var body = ext.SurfaceBodies[1];
        body.Name = "BX_Box";

        // Outer vertical corner fillets
        var fillet = _def.Features.FilletFeatures.AddSimple(VerticalEdges(body, (_, _) => true), "BX_CornerRadius");
        fillet.Name = "BX_BodyFillet";

        // Shell: open the top face, wall BX_Wall, bottom BX_Bottom
        body = FindBody("BX_Box");
        FindTopBottomFaces(body, out var top, out var bottom);
        var open = _app.TransientObjects.CreateFaceCollection();
        open.Add(top);
        var sd = _def.Features.ShellFeatures.CreateShellDefinition(open, "BX_Wall", Inv.ShellDirectionEnum.kInsideShellDirection);
        var bottomFaces = _app.TransientObjects.CreateFaceCollection();
        bottomFaces.Add(bottom);
        sd.AddThicknessFaceSet(bottomFaces, "BX_Bottom");
        var shell = _def.Features.ShellFeatures.Add(sd);
        shell.Name = "BX_BodyShell";
    }

    // ------------------------------------------------------------ Lid

    private void BuildLid()
    {
        var plane = _def.WorkPlanes.AddByPlaneAndOffset(_def.WorkPlanes[3], "BX_Height", false);
        plane.Name = "BX_LidPlane";
        plane.Visible = false;

        // Lid plate
        var sk = _def.Sketches.Add(plane);
        sk.Name = "BX_LidSketch";
        CenteredRectangle(sk, BoxParams.Length, BoxParams.Width, Cm(BoxParams.Length), Cm(BoxParams.Width));
        var ed = _def.Features.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), Inv.PartFeatureOperationEnum.kNewBodyOperation);
        ed.SetDistanceExtent("BX_LidThickness", Inv.PartFeatureExtentDirectionEnum.kPositiveExtentDirection);
        var ext = _def.Features.ExtrudeFeatures.Add(ed);
        ext.Name = "BX_LidExtrude";
        var lid = ext.SurfaceBodies[1];
        lid.Name = "BX_Lid";
        _def.Features.FilletFeatures.AddSimple(VerticalEdges(lid, (_, _) => true), "BX_CornerRadius").Name = "BX_LidFillet";

        // The lid lip points down, joined to the lid body (not to the body)
        var lipSketch = _def.Sketches.Add(plane);
        lipSketch.Name = "BX_LipSketch";
        const string lipLen = "BX_Length - 2 * BX_Wall - 2 * BX_LidClearance";
        const string lipWid = "BX_Width - 2 * BX_Wall - 2 * BX_LidClearance";
        CenteredRectangle(lipSketch, lipLen, lipWid,
            Cm(BoxParams.Length) - 2 * Cm(BoxParams.Wall) - 2 * Cm(BoxParams.LidClearance),
            Cm(BoxParams.Width) - 2 * Cm(BoxParams.Wall) - 2 * Cm(BoxParams.LidClearance));
        var led = _def.Features.ExtrudeFeatures.CreateExtrudeDefinition(lipSketch.Profiles.AddForSolid(), Inv.PartFeatureOperationEnum.kJoinOperation);
        led.SetDistanceExtent("BX_LidLip", Inv.PartFeatureExtentDirectionEnum.kNegativeExtentDirection);
        var participants = _app.TransientObjects.CreateObjectCollection();
        participants.Add(FindBody("BX_Lid"));
        led.AffectedBodies = participants;
        var lipExt = _def.Features.ExtrudeFeatures.Add(led);
        lipExt.Name = "BX_LipExtrude";

        // Lip corner fillets: the vertical edges of the lid body that are below the lid plane (z < BX_Height)
        double zPlane = Cm(BoxParams.Height);
        var lipEdges = VerticalEdges(FindBody("BX_Lid"), (a, b) => Math.Min(a.Z, b.Z) < zPlane - Tol);
        _def.Features.FilletFeatures.AddSimple(lipEdges, "BX_CornerRadius - BX_Wall - BX_LidClearance").Name = "BX_LipFillet";
    }

    // ------------------------------------------------------------ Utilities

    /// <summary>
    /// A rectangle centered at the sketch origin, with two dimensions bound to expressions (Parameter names).
    /// Note: the last parameter of AddTwoPointDistance is DrivenDimension; it must be false so the dimension drives the shape (driving).
    /// </summary>
    private void CenteredRectangle(Inv.PlanarSketch sk, string lengthExpr, string widthExpr, double initLenCm, double initWidCm)
    {
        // Project the Center Point of the Part into the sketch to get a SketchPoint at the origin (sk.OriginPoint is a 3D point, it cannot be used here).
        var origin = (Inv.SketchPoint)sk.AddByProjectingEntity(_def.WorkPoints[1]);
        var entities = sk.SketchLines.AddAsTwoPointCenteredRectangle(origin, _tg.CreatePoint2d(initLenCm / 2, initWidCm / 2));
        var lines = new List<Inv.SketchLine>();
        foreach (object o in entities)
            if (o is Inv.SketchLine l && !l.Construction) lines.Add(l);

        var horizontal = lines.First(l => Math.Abs(l.StartSketchPoint.Geometry.Y - l.EndSketchPoint.Geometry.Y) < Tol);
        var vertical = lines.First(l => Math.Abs(l.StartSketchPoint.Geometry.X - l.EndSketchPoint.Geometry.X) < Tol);

        var dl = sk.DimensionConstraints.AddTwoPointDistance(horizontal.StartSketchPoint, horizontal.EndSketchPoint,
            Inv.DimensionOrientationEnum.kHorizontalDim, _tg.CreatePoint2d(0, -initWidCm / 2 - 1), false);
        dl.Parameter.Expression = lengthExpr;

        var dw = sk.DimensionConstraints.AddTwoPointDistance(vertical.StartSketchPoint, vertical.EndSketchPoint,
            Inv.DimensionOrientationEnum.kVerticalDim, _tg.CreatePoint2d(initLenCm / 2 + 1, 0), false);
        dw.Parameter.Expression = widthExpr;
    }

    private Inv.SurfaceBody FindBody(string name)
    {
        foreach (Inv.SurfaceBody b in _def.SurfaceBodies)
            if (b.Name == name) return b;
        throw new InvalidOperationException($"Body '{name}' not found.");
    }

    private Inv.EdgeCollection VerticalEdges(Inv.SurfaceBody body, Func<Inv.Point, Inv.Point, bool> predicate)
    {
        var edges = _app.TransientObjects.CreateEdgeCollection();
        foreach (Inv.Edge e in body.Edges)
        {
            if (e.GeometryType != Inv.CurveTypeEnum.kLineSegmentCurve) continue;
            var a = e.StartVertex.Point;
            var b = e.StopVertex.Point;
            if (Math.Abs(a.X - b.X) < Tol && Math.Abs(a.Y - b.Y) < Tol && Math.Abs(a.Z - b.Z) > Tol && predicate(a, b))
                edges.Add(e);
        }
        if (edges.Count == 0) throw new InvalidOperationException("No vertical edges found to fillet.");
        return edges;
    }

    /// <summary>The highest and lowest horizontal planes of the body.</summary>
    private static void FindTopBottomFaces(Inv.SurfaceBody body, out Inv.Face top, out Inv.Face bottom)
    {
        Inv.Face? t = null, b = null;
        double zt = double.MinValue, zb = double.MaxValue;
        foreach (Inv.Face f in body.Faces)
        {
            if (f.SurfaceType != Inv.SurfaceTypeEnum.kPlaneSurface) continue;
            var plane = (Inv.Plane)f.Geometry;
            if (Math.Abs(plane.Normal.Z) < 0.999) continue;
            double z = plane.RootPoint.Z;
            if (z > zt) { zt = z; t = f; }
            if (z < zb) { zb = z; b = f; }
        }
        top = t ?? throw new InvalidOperationException("Top face of the box body not found.");
        bottom = b ?? throw new InvalidOperationException("Bottom face of the box body not found.");
    }
}
