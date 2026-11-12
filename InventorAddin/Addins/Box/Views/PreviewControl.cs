using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using InventorAddin.Models;

namespace InventorAddin.Views;

/// <summary>
/// Draws a <see cref="Scene"/> onto a DrawingContext, scaled with the right proportions (no distortion) and with its own background
/// so it is readable in both the light and the dark theme of Inventor. It is only an illustrative diagram, not an Inventor render.
/// </summary>
public sealed class PreviewControl : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(Scene), typeof(PreviewControl),
        new FrameworkPropertyMetadata(Scene.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(string), typeof(PreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ErrorParamsProperty = DependencyProperty.Register(
        nameof(ErrorParams), typeof(IReadOnlySet<string>), typeof(PreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Scene Scene { get => (Scene)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public string? Highlight { get => (string?)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public IReadOnlySet<string>? ErrorParams { get => (IReadOnlySet<string>?)GetValue(ErrorParamsProperty); set => SetValue(ErrorParamsProperty, value); }

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)));
    private static readonly Brush BodyFill = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xE8)));
    private static readonly Brush LidFill = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xD3, 0xB0)));
    private static readonly Brush GapFill = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xF2, 0x8C, 0x28)));
    private static readonly Brush OverlayFill = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x84, 0xFF)));
    private static readonly Brush ErrorFill = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xD3, 0x2F, 0x2F)));
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x4F)));
    private static readonly Brush InnerInk = Frozen(new SolidColorBrush(Color.FromRgb(0x7A, 0x8A, 0xA0)));
    private static readonly Brush DimInk = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));
    private static readonly Typeface Face = new("Segoe UI");

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, new Rect(size));
        var scene = Scene;
        if (scene.Shapes.Count == 0 || size.Width < 20 || size.Height < 20) return;

        const double pad = 8;
        double scale = Math.Min((size.Width - 2 * pad) / scene.Width, (size.Height - 2 * pad) / scene.Height);
        double ox = (size.Width - scene.Width * scale) / 2 - scene.MinX * scale;
        double oy = (size.Height - scene.Height * scale) / 2 - scene.MinY * scale;
        Point P(double x, double y) => new(ox + x * scale, oy + y * scale);

        var hl = Highlight;
        var errors = ErrorParams;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Two passes: shapes first, labels after so the text is not covered
        foreach (var shape in scene.Shapes)
        {
            bool isHl = shape.Param != null && shape.Param == hl;
            bool isErr = shape.Param != null && errors != null && errors.Contains(shape.Param);
            switch (shape)
            {
                case RectShape r:
                    DrawRect(dc, r, P, scale, isHl, isErr);
                    break;
                case PolyShape poly:
                    DrawPoly(dc, poly, P);
                    break;
                case LineShape l:
                    dc.DrawLine(new Pen(isErr ? Red : isHl ? Accent : DimInk, isHl || isErr ? 2.2 : 1), P(l.X1, l.Y1), P(l.X2, l.Y2));
                    break;
            }
        }

        foreach (var label in scene.Shapes.OfType<LabelShape>())
        {
            bool isHl = label.Param != null && label.Param == hl;
            bool isErr = label.Param != null && errors != null && errors.Contains(label.Param);
            var brush = isErr ? Red : isHl ? Accent : Ink;
            var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11, brush, pixelsPerDip);
            if (isHl || isErr) text.SetFontWeight(FontWeights.SemiBold);
            var p = P(label.X, label.Y);
            double x = label.Anchor switch
            {
                LabelAnchor.Left => p.X + 3,
                LabelAnchor.Right => p.X - text.Width - 3,
                _ => p.X - text.Width / 2,
            };
            double y = p.Y - text.Height / 2 + label.Dy * (text.Height * 0.5 + 4);
            dc.DrawText(text, new Point(x, y));
        }
    }

    private static void DrawRect(DrawingContext dc, RectShape r, Func<double, double, Point> p, double scale, bool isHl, bool isErr)
    {
        if (r.Style == ShapeStyle.Overlay && !isHl && !isErr) return;

        var a = p(r.X, r.Y);
        double w = r.W * scale, h = r.H * scale;
        // Walls and clearances are very thin: draw them with a minimum thickness of a few pixels
        double minPx = r.Style == ShapeStyle.Gap ? 2.5 : r.Style == ShapeStyle.Overlay ? 3 : 0;
        if (minPx > 0)
        {
            if (w < minPx) { a.X -= (minPx - w) / 2; w = minPx; }
            if (h < minPx) { a.Y -= (minPx - h) / 2; h = minPx; }
        }
        var rect = new Rect(a.X, a.Y, Math.Max(w, 0), Math.Max(h, 0));
        double rad = r.Radius * scale;

        switch (r.Style)
        {
            case ShapeStyle.BodyFill:
                dc.DrawRoundedRectangle(BodyFill, null, rect, rad, rad);
                break;
            case ShapeStyle.Outline:
                dc.DrawRoundedRectangle(null, new Pen(Ink, 1.5), rect, rad, rad);
                break;
            case ShapeStyle.Inner:
                dc.DrawRoundedRectangle(null, new Pen(isErr ? Red : isHl ? Accent : InnerInk, isHl || isErr ? 2 : 1), rect, rad, rad);
                break;
            case ShapeStyle.Gap:
                dc.DrawRoundedRectangle(isHl ? Accent : GapFill, null, rect, rad, rad);
                break;
            case ShapeStyle.Overlay:
                dc.DrawRoundedRectangle(isErr ? ErrorFill : OverlayFill, null, rect, rad, rad);
                break;
        }
    }

    private static void DrawPoly(DrawingContext dc, PolyShape poly, Func<double, double, Point> p)
    {
        if (poly.Points.Count < 3) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var first = poly.Points[0];
            ctx.BeginFigure(p(first.X, first.Y), poly.Style != ShapeStyle.Outline, true);
            for (int i = 1; i < poly.Points.Count; i++) ctx.LineTo(p(poly.Points[i].X, poly.Points[i].Y), true, false);
        }
        geo.Freeze();
        switch (poly.Style)
        {
            case ShapeStyle.BodyFill: dc.DrawGeometry(BodyFill, null, geo); break;
            case ShapeStyle.LidFill: dc.DrawGeometry(LidFill, null, geo); break;
            default: dc.DrawGeometry(null, new Pen(Ink, 1.5) { LineJoin = PenLineJoin.Round }, geo); break;
        }
    }
}
