using System.Collections.Generic;

namespace InventorAddin.Models;

public enum ShapeStyle
{
    BodyFill,   // cut area of the box body
    LidFill,    // cut area of the lid
    Outline,    // outline
    Inner,      // inner wall line / lid lip
    Gap,        // clearance
    Dim,        // dimension line
    Overlay,    // drawn only when the matching parameter has focus
}

public enum LabelAnchor { Center, Left, Right }

/// <summary>Preview shape. Coordinates in mm, the y axis points down (screen style). Param is the name of the related parameter, to highlight it.</summary>
public abstract record Shape(ShapeStyle Style, string? Param);

public sealed record RectShape(double X, double Y, double W, double H, double Radius, ShapeStyle Style, string? Param = null)
    : Shape(Style, Param);

public sealed record LineShape(double X1, double Y1, double X2, double Y2, ShapeStyle Style, string? Param = null)
    : Shape(Style, Param);

public sealed record PolyShape(IReadOnlyList<(double X, double Y)> Points, ShapeStyle Style, string? Param = null)
    : Shape(Style, Param);

/// <param name="Dy">-1: shifted up, +1: shifted down, 0: centered (in pixels on the drawing layer).</param>
public sealed record LabelShape(double X, double Y, string Text, string? Param, LabelAnchor Anchor = LabelAnchor.Center, int Dy = 0)
    : Shape(ShapeStyle.Dim, Param);

public sealed record Scene(double MinX, double MinY, double Width, double Height, IReadOnlyList<Shape> Shapes)
{
    public static readonly Scene Empty = new(0, 0, 1, 1, new Shape[0]);
}
