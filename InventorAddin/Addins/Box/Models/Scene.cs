using System.Collections.Generic;

namespace InventorAddin.Models;

public enum ShapeStyle
{
    BodyFill,   // vùng cắt của thân hộp
    LidFill,    // vùng cắt của nắp
    Outline,    // đường viền
    Inner,      // đường thành trong / gờ nắp
    Gap,        // khe hở
    Dim,        // đường kích thước
    Overlay,    // chỉ vẽ khi thông số tương ứng đang được focus
}

public enum LabelAnchor { Center, Left, Right }

/// <summary>Hình xem trước. Tọa độ theo mm, trục y hướng xuống (kiểu màn hình). Param là tên thông số liên quan để tô nổi bật.</summary>
public abstract record Shape(ShapeStyle Style, string? Param);

public sealed record RectShape(double X, double Y, double W, double H, double Radius, ShapeStyle Style, string? Param = null)
    : Shape(Style, Param);

public sealed record LineShape(double X1, double Y1, double X2, double Y2, ShapeStyle Style, string? Param = null)
    : Shape(Style, Param);

public sealed record PolyShape(IReadOnlyList<(double X, double Y)> Points, ShapeStyle Style, string? Param = null)
    : Shape(Style, Param);

/// <param name="Dy">-1: lệch lên trên, +1: lệch xuống dưới, 0: giữa (tính bằng pixel ở lớp vẽ).</param>
public sealed record LabelShape(double X, double Y, string Text, string? Param, LabelAnchor Anchor = LabelAnchor.Center, int Dy = 0)
    : Shape(ShapeStyle.Dim, Param);

public sealed record Scene(double MinX, double MinY, double Width, double Height, IReadOnlyList<Shape> Shapes)
{
    public static readonly Scene Empty = new(0, 0, 1, 1, new Shape[0]);
}
