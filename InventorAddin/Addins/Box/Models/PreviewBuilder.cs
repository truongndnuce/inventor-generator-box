using System;
using System.Collections.Generic;
using System.Globalization;

namespace InventorAddin.Models;

/// <summary>
/// Tính hình cho canvas: nhận bộ thông số, trả về danh sách hình và nhãn. Không gọi Inventor/WPF nên test được.
/// Đây chỉ là sơ đồ minh họa, vẽ đúng tỷ lệ giữa các kích thước.
/// </summary>
public static class PreviewBuilder
{
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static double V(IReadOnlyDictionary<string, double> v, string name) =>
        v.TryGetValue(name, out var x) ? x : BoxParams.Get(name).Default;

    /// <summary>Đường kích thước nằm ngang hoặc đứng có hai vạch đầu mút.</summary>
    private static void Dimension(List<Shape> s, double x1, double y1, double x2, double y2, double tick, string param)
    {
        s.Add(new LineShape(x1, y1, x2, y2, ShapeStyle.Dim, param));
        if (Math.Abs(y1 - y2) < 1e-9) // ngang
        {
            s.Add(new LineShape(x1, y1 - tick, x1, y1 + tick, ShapeStyle.Dim, param));
            s.Add(new LineShape(x2, y2 - tick, x2, y2 + tick, ShapeStyle.Dim, param));
        }
        else // đứng
        {
            s.Add(new LineShape(x1 - tick, y1, x1 + tick, y1, ShapeStyle.Dim, param));
            s.Add(new LineShape(x2 - tick, y2, x2 + tick, y2, ShapeStyle.Dim, param));
        }
    }

    /// <summary>Khung nhìn mặt bằng (từ trên xuống).</summary>
    public static Scene Plan(IReadOnlyDictionary<string, double> v)
    {
        double len = V(v, BoxParams.Length), wid = V(v, BoxParams.Width);
        double wall = V(v, BoxParams.Wall), r = V(v, BoxParams.CornerRadius), clr = V(v, BoxParams.LidClearance);
        double m = Math.Max(len, wid) * 0.2;
        double tick = m * 0.08;
        var s = new List<Shape>();

        s.Add(new RectShape(0, 0, len, wid, r, ShapeStyle.BodyFill));
        s.Add(new RectShape(0, 0, len, wid, r, ShapeStyle.Outline));
        s.Add(new RectShape(wall, wall, len - 2 * wall, wid - 2 * wall, Math.Max(r - wall, 0), ShapeStyle.Inner, BoxParams.Wall));
        double lip = wall + clr;
        s.Add(new RectShape(lip, lip, len - 2 * lip, wid - 2 * lip, Math.Max(r - lip, 0), ShapeStyle.Gap, BoxParams.LidClearance));

        // Nổi bật khi focus
        s.Add(new RectShape(0, 0, len, wall, 0, ShapeStyle.Overlay, BoxParams.Wall));
        s.Add(new RectShape(0, wid - wall, len, wall, 0, ShapeStyle.Overlay, BoxParams.Wall));
        s.Add(new RectShape(0, 0, r, r, 0, ShapeStyle.Overlay, BoxParams.CornerRadius));
        s.Add(new RectShape(len - r, 0, r, r, 0, ShapeStyle.Overlay, BoxParams.CornerRadius));
        s.Add(new RectShape(0, wid - r, r, r, 0, ShapeStyle.Overlay, BoxParams.CornerRadius));
        s.Add(new RectShape(len - r, wid - r, r, r, 0, ShapeStyle.Overlay, BoxParams.CornerRadius));

        double yDim = wid + m * 0.45, xDim = len + m * 0.45;
        Dimension(s, 0, yDim, len, yDim, tick, BoxParams.Length);
        s.Add(new LabelShape(len / 2, yDim, $"Length {F(len)}", BoxParams.Length, LabelAnchor.Center, 1));
        Dimension(s, xDim, 0, xDim, wid, tick, BoxParams.Width);
        s.Add(new LabelShape(xDim, wid / 2, $"Width {F(wid)}", BoxParams.Width, LabelAnchor.Left, 0));
        s.Add(new LabelShape(wall + m * 0.1, wall + m * 0.1, $"R{F(r)}", BoxParams.CornerRadius, LabelAnchor.Left, 0));

        return new Scene(-m * 0.3, -m * 0.3, len + m * 1.9, wid + m * 1.0, s);
    }

    /// <summary>Khung nhìn mặt cắt dọc qua tâm hộp, thân phía dưới, nắp phía trên.</summary>
    public static Scene Section(IReadOnlyDictionary<string, double> v)
    {
        double len = V(v, BoxParams.Length), hgt = V(v, BoxParams.Height);
        double wall = V(v, BoxParams.Wall), bottom = V(v, BoxParams.Bottom);
        double lt = V(v, BoxParams.LidThickness), lip = V(v, BoxParams.LidLip), clr = V(v, BoxParams.LidClearance);
        double m = Math.Max(len, hgt + lt) * 0.2;
        double tick = m * 0.08;
        var s = new List<Shape>();

        // y hướng xuống: nắp từ y=0..lt, thân từ y=lt..lt+hgt (khớp mặt trên thân với đáy nắp)
        double yTopBody = lt, yBottom = lt + hgt;

        var body = new (double, double)[]
        {
            (0, yTopBody), (wall, yTopBody), (wall, yBottom - bottom), (len - wall, yBottom - bottom),
            (len - wall, yTopBody), (len, yTopBody), (len, yBottom), (0, yBottom),
        };
        s.Add(new PolyShape(body, ShapeStyle.BodyFill));
        s.Add(new PolyShape(body, ShapeStyle.Outline));

        double lx0 = wall + clr, lx1 = len - wall - clr;
        var lid = new (double, double)[]
        {
            (0, 0), (len, 0), (len, lt), (lx1, lt), (lx1, lt + lip), (lx0, lt + lip), (lx0, lt), (0, lt),
        };
        s.Add(new PolyShape(lid, ShapeStyle.LidFill));
        s.Add(new PolyShape(lid, ShapeStyle.Outline));

        // Khe hở giữa gờ và thành trong (vẽ ở lớp vẽ với độ dày tối thiểu vài pixel)
        s.Add(new RectShape(wall, yTopBody, clr, lip, 0, ShapeStyle.Gap, BoxParams.LidClearance));
        s.Add(new RectShape(lx1, yTopBody, clr, lip, 0, ShapeStyle.Gap, BoxParams.LidClearance));

        // Nổi bật khi focus
        s.Add(new RectShape(0, yTopBody, wall, hgt, 0, ShapeStyle.Overlay, BoxParams.Wall));
        s.Add(new RectShape(len - wall, yTopBody, wall, hgt, 0, ShapeStyle.Overlay, BoxParams.Wall));
        s.Add(new RectShape(wall, yBottom - bottom, len - 2 * wall, bottom, 0, ShapeStyle.Overlay, BoxParams.Bottom));
        s.Add(new RectShape(0, 0, len, lt, 0, ShapeStyle.Overlay, BoxParams.LidThickness));
        s.Add(new RectShape(lx0, lt, lx1 - lx0, lip, 0, ShapeStyle.Overlay, BoxParams.LidLip));

        // Kích thước
        double xLeft = -m * 0.35, xRight = len + m * 0.35;
        Dimension(s, xLeft, yTopBody, xLeft, yBottom, tick, BoxParams.Height);
        s.Add(new LabelShape(xLeft - tick, (yTopBody + yBottom) / 2, $"Height {F(hgt)}", BoxParams.Height, LabelAnchor.Right, 0));

        Dimension(s, xRight, 0, xRight, lt, tick, BoxParams.LidThickness);
        s.Add(new LabelShape(xRight + tick * 2, lt / 2, $"Lid {F(lt)}", BoxParams.LidThickness, LabelAnchor.Left, 0));
        Dimension(s, xRight, lt, xRight, lt + lip, tick, BoxParams.LidLip);
        s.Add(new LabelShape(xRight + tick * 2, lt + lip / 2 + m * 0.08, $"Lip {F(lip)}", BoxParams.LidLip, LabelAnchor.Left, 1));
        Dimension(s, xRight, yBottom - bottom, xRight, yBottom, tick, BoxParams.Bottom);
        s.Add(new LabelShape(xRight + tick * 2, yBottom - bottom / 2, $"Bottom {F(bottom)}", BoxParams.Bottom, LabelAnchor.Left, 0));

        double yWall = yBottom + m * 0.25;
        Dimension(s, 0, yWall, wall, yWall, tick, BoxParams.Wall);
        s.Add(new LabelShape(wall / 2, yWall, $"Wall {F(wall)}", BoxParams.Wall, LabelAnchor.Left, 1));
        double yClr = -m * 0.2;
        Dimension(s, wall, yClr, wall + clr, yClr, tick, BoxParams.LidClearance);
        s.Add(new LabelShape(wall + clr + tick, yClr, $"Gap {F(clr)}", BoxParams.LidClearance, LabelAnchor.Left, -1));
        double yLen = yBottom + m * 0.6;
        Dimension(s, 0, yLen, len, yLen, tick, BoxParams.Length);
        s.Add(new LabelShape(len / 2, yLen, $"Length {F(len)}", BoxParams.Length, LabelAnchor.Center, 1));

        return new Scene(-m * 1.1, -m * 0.5, len + m * 2.4, hgt + lt + m * 1.3, s);
    }
}
