using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InventorAddin.Host;
using Inv = Inventor;

namespace InventorAddin.Services;

/// <summary>Kết quả dựng bản vẽ. Dim hụt (không tìm thấy cạnh) chỉ làm giảm <see cref="Dimensions"/>, không làm hỏng bản vẽ.</summary>
public sealed record DrawingResult(bool Ok, string Sheet, double ScaleDenominator, int Views, int Dimensions, string? Warning, bool DwgOk = false);

/// <summary>
/// Dựng bản vẽ 2D của một Part theo quy ước bản vẽ kỹ thuật cơ khí chung (ISO 128 / ISO 129 / ISO 5455 / ISO 5457, TCVN tương đương):
/// chiếu góc thứ nhất (hình chiếu bằng nằm DƯỚI hình chiếu đứng, hình chiếu cạnh nằm BÊN PHẢI), khổ A4..A0 nhỏ nhất vừa
/// với tỉ lệ chuẩn không nhỏ hơn 1:20, mặt cắt A-A qua tâm, hình iso, dim kích thước bao và chuỗi dim chiều cao trên mặt cắt.
/// Nét vẽ, mũi tên, chiều cao chữ do template ISO.idw đảm nhiệm (đúng tiêu chuẩn), code không ghi đè.
/// Chỉ được gọi trên UI thread của Inventor.
/// </summary>
internal sealed class DrawingBuilder
{
    private const string PdfTranslatorId = "{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}";
    private const string DwgTranslatorId = "{C24E3AC2-122E-11D5-8E91-0010B541CD80}"; // "Translator: DWG" (Inventor 2027.2), chạy được không cần tùy chọn

    // ISO 5457 (nằm ngang, cm) và ISO 5455 (tỉ lệ ưu tiên, từ lớn đến nhỏ).
    private static readonly (Inv.DrawingSheetSizeEnum Size, double W, double H, string Name)[] Sheets =
    {
        (Inv.DrawingSheetSizeEnum.kA4DrawingSheetSize, 29.7, 21.0, "A4"),
        (Inv.DrawingSheetSizeEnum.kA3DrawingSheetSize, 42.0, 29.7, "A3"),
        (Inv.DrawingSheetSizeEnum.kA2DrawingSheetSize, 59.4, 42.0, "A2"),
        (Inv.DrawingSheetSizeEnum.kA1DrawingSheetSize, 84.1, 59.4, "A1"),
        (Inv.DrawingSheetSizeEnum.kA0DrawingSheetSize, 118.9, 84.1, "A0"),
    };
    private static readonly double[] Scales = { 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001 };

    private const double MinPreferredScale = 0.05; // không nhỏ hơn 1:20 nếu còn khổ lớn hơn để chọn
    private const double ViewGap = 3.0;            // cm giữa các hình chiếu: chừa chỗ cho hai hàng dim
    private const double LeftRoom = 4.5;           // cm: lề đóng gáy (2) + cột dim bên trái
    private const double TopRoom = 2.5;
    private const double FirstDimOffset = 1.5;     // >= 10 mm kể từ hình (ISO 129)
    private const double SecondDimOffset = 2.6;

    private readonly Inv.Application _app;

    public DrawingBuilder(Inv.Application app) => _app = app;

    /// <param name="part">Part đã dựng và đã lưu, đang mở.</param>
    /// <param name="zUp">Trục đứng của mô hình là Z (hộp) hay Y (máy biến áp).</param>
    /// <param name="sectionLevelsMm">Các mức cao (mm từ đáy mô hình) để ghi chuỗi dim trên mặt cắt; rỗng thì bỏ qua.</param>
    /// <param name="dwgPath">Nếu có, xuất thêm bản vẽ dạng DWG. Lỗi DWG chỉ thành cảnh báo, không làm hỏng PDF.</param>
    public DrawingResult Build(Inv.PartDocument part, bool zUp, IReadOnlyList<double> sectionLevelsMm, string pdfPath, string? dwgPath = null)
    {
        Inv.DrawingDocument? dd = null;
        try
        {
            var box = part.ComponentDefinition.RangeBox;
            double sx = box.MaxPoint.X - box.MinPoint.X, sy = box.MaxPoint.Y - box.MinPoint.Y, sz = box.MaxPoint.Z - box.MinPoint.Z;
            double length = sx, height = zUp ? sz : sy, depth = zUp ? sy : sz; // cm

            var (sheetInfo, scale) = ChooseSheet(length, height, depth);

            var template = _app.FileManager.GetTemplateFile(Inv.DocumentTypeEnum.kDrawingDocumentObject,
                Inv.SystemOfMeasureEnum.kMetricSystemOfMeasure, Inv.DraftingStandardEnum.kISO_DraftingStandard);
            dd = (Inv.DrawingDocument)_app.Documents.Add(Inv.DocumentTypeEnum.kDrawingDocumentObject, template, true);
            var sheet = dd.ActiveSheet;
            sheet.Size = sheetInfo.Size;
            var tg = _app.TransientGeometry;
            var model = (Inv._Document)part;
            var warnings = new List<string>();

            double ls = length * scale, hs = height * scale, ds = depth * scale;
            double fx = LeftRoom + ls / 2, fy = sheet.Height - TopRoom - hs / 2;
            var frontOrientation = zUp ? Inv.ViewOrientationTypeEnum.kBottomViewOrientation : Inv.ViewOrientationTypeEnum.kFrontViewOrientation;
            const Inv.DrawingViewStyleEnum hidden = Inv.DrawingViewStyleEnum.kHiddenLineRemovedDrawingViewStyle;
            const Inv.DrawingViewStyleEnum fromBase = Inv.DrawingViewStyleEnum.kFromBaseDrawingViewStyle;

            var front = sheet.DrawingViews.AddBaseView(model, tg.CreatePoint2d(fx, fy), scale, frontOrientation, hidden, "", Type.Missing, Type.Missing);
            int views = 1;
            var top = sheet.DrawingViews.AddProjectedView(front, tg.CreatePoint2d(fx, fy - hs / 2 - ViewGap - ds / 2), fromBase, Type.Missing);
            views++;
            double sideX = fx + ls / 2 + ViewGap + ds / 2;
            var side = sheet.DrawingViews.AddProjectedView(front, tg.CreatePoint2d(sideX, fy), fromBase, Type.Missing);
            views++;

            Inv.SectionDrawingView? section = null;
            try
            {
                // Toạ độ sketch của view theo kích thước mô hình (chưa nhân tỉ lệ), gốc ở tâm view.
                var sk = front.Sketches.Add();
                sk.Edit();
                double half = (front.Height / 2 + 1) / scale;
                sk.SketchLines.AddByTwoPoints(tg.CreatePoint2d(0, -half), tg.CreatePoint2d(0, half));
                sk.ExitEdit();
                section = sheet.DrawingViews.AddSectionView(front, sk, tg.CreatePoint2d(sideX + ds / 2 + ViewGap + ds / 2, fy),
                    fromBase, Type.Missing, true, "A", false, true, Type.Missing);
                views++;
            }
            catch (Exception ex)
            {
                Log.Warn("Drawing: section view failed: " + ex.Message);
                warnings.Add("section view failed");
            }

            // Hình iso ở khoảng trống còn lại (dưới mặt cắt, trên khung tên).
            try
            {
                sheet.DrawingViews.AddBaseView(model, tg.CreatePoint2d(sheet.Width * 0.72, sheet.Height * 0.3), scale / 2,
                    Inv.ViewOrientationTypeEnum.kIsoTopRightViewOrientation, hidden, "", Type.Missing, Type.Missing);
                views++;
            }
            catch (Exception ex) { Log.Warn("Drawing: iso view failed: " + ex.Message); }

            int dims = 0;
            dims += Guard(() => Overall(sheet, tg, front, vertical: true, horizontal: false, FirstDimOffset), "front height", warnings);
            dims += Guard(() => Overall(sheet, tg, front, vertical: false, horizontal: true, SecondDimOffset), "front length", warnings);
            dims += Guard(() => Overall(sheet, tg, top, vertical: true, horizontal: false, FirstDimOffset), "top depth", warnings);
            dims += Guard(() => Overall(sheet, tg, side, vertical: false, horizontal: true, FirstDimOffset), "side depth", warnings);
            if (section != null && sectionLevelsMm.Count >= 2)
                dims += Guard(() => HeightChain(sheet, tg, (Inv.DrawingView)section, sectionLevelsMm, scale, FirstDimOffset), "section chain", warnings);

            ExportPdf(dd, pdfPath);

            bool dwgOk = false;
            if (dwgPath != null)
            {
                try
                {
                    ExportDwg(dd, dwgPath);
                    dwgOk = File.Exists(dwgPath);
                    if (!dwgOk) warnings.Add("DWG export produced no file");
                }
                catch (Exception ex)
                {
                    Log.Warn("Drawing: DWG export failed: " + ex.Message);
                    warnings.Add("DWG export failed");
                }
            }

            return new DrawingResult(File.Exists(pdfPath), sheetInfo.Name, Math.Round(1 / scale, 2), views, dims,
                warnings.Count > 0 ? string.Join("; ", warnings) : null, dwgOk);
        }
        catch (Exception ex)
        {
            Log.Error("Drawing failed", ex);
            return new DrawingResult(false, "", 0, 0, 0, ex.Message);
        }
        finally
        {
            try { dd?.Close(true); } // bỏ qua hỏi lưu: bản vẽ chỉ xuất PDF
            catch (Exception ex) { Log.Warn("Could not close the drawing: " + ex.Message); }
        }
    }

    // ---------------------------------------------------------------- khổ giấy / tỉ lệ

    private static ((Inv.DrawingSheetSizeEnum Size, double W, double H, string Name) Sheet, double Scale) ChooseSheet(double l, double h, double d)
    {
        foreach (var sheet in Sheets)
        {
            foreach (var scale in Scales)
            {
                if (Fits(sheet.W, sheet.H, scale, l, h, d))
                {
                    if (scale >= MinPreferredScale) return (sheet, scale);
                    break; // khổ này chỉ vừa ở tỉ lệ quá nhỏ: thử khổ lớn hơn
                }
            }
        }
        var last = Sheets[^1];
        foreach (var scale in Scales)
            if (Fits(last.W, last.H, scale, l, h, d)) return (last, scale);
        return (last, Scales[^1]);
    }

    private static bool Fits(double w, double h, double scale, double l, double ht, double d) =>
        LeftRoom + (l + 2 * d) * scale + 2 * ViewGap + 1.0 <= w && TopRoom + (ht + d) * scale + ViewGap + 1.0 <= h;

    // ---------------------------------------------------------------- dim

    private static int Guard(Func<int> action, string what, List<string> warnings)
    {
        try { return action(); }
        catch (Exception ex)
        {
            Log.Warn($"Drawing: dimension '{what}' failed: {ex.Message}");
            warnings.Add($"dim '{what}' failed");
            return 0;
        }
    }

    /// <summary>Dim kích thước bao: hai đường ngoài cùng theo mỗi phương (mọi loại curve, kể cả vòng tròn nhìn nghiêng).</summary>
    private static int Overall(Inv.Sheet sheet, Inv.TransientGeometry tg, Inv.DrawingView view, bool vertical, bool horizontal, double offset)
    {
        var (curves, boxes) = ViewCurves(view);
        if (curves.Count == 0) return 0;
        int iBottom = Extreme(boxes, b => -b.MinPoint.Y), iTop = Extreme(boxes, b => b.MaxPoint.Y);
        int iLeft = Extreme(boxes, b => -b.MinPoint.X), iRight = Extreme(boxes, b => b.MaxPoint.X);
        double xl = boxes[iLeft].MinPoint.X, xr = boxes[iRight].MaxPoint.X, yb = boxes[iBottom].MinPoint.Y, yt = boxes[iTop].MaxPoint.Y;
        var dimensions = sheet.DrawingDimensions.GeneralDimensions;
        int made = 0;

        Inv.GeometryIntent At(int i, double x, double y) => sheet.CreateGeometryIntent(curves[i], tg.CreatePoint2d(x, y));
        static double MidX(Inv.Box2d r) => (r.MinPoint.X + r.MaxPoint.X) / 2;
        static double MidY(Inv.Box2d r) => (r.MinPoint.Y + r.MaxPoint.Y) / 2;

        if (vertical)
        {
            dimensions.AddLinear(tg.CreatePoint2d(xl - offset, (yt + yb) / 2), At(iBottom, MidX(boxes[iBottom]), yb), At(iTop, MidX(boxes[iTop]), yt),
                Inv.DimensionTypeEnum.kVerticalDimensionType, true, Type.Missing, Type.Missing);
            made++;
        }
        if (horizontal)
        {
            dimensions.AddLinear(tg.CreatePoint2d((xl + xr) / 2, yb - offset), At(iLeft, xl, MidY(boxes[iLeft])), At(iRight, xr, MidY(boxes[iRight])),
                Inv.DimensionTypeEnum.kHorizontalDimensionType, true, Type.Missing, Type.Missing);
            made++;
        }
        return made;
    }

    /// <summary>Chuỗi dim chiều cao ở mép phải mặt cắt (vd. đế, thân, nắp). Mức không có cạnh tương ứng thì bỏ cả chuỗi.</summary>
    private static int HeightChain(Inv.Sheet sheet, Inv.TransientGeometry tg, Inv.DrawingView view, IReadOnlyList<double> levelsMm, double scale, double offset)
    {
        var (curves, boxes) = ViewCurves(view);
        if (curves.Count == 0) return 0;
        double yRef = boxes.Min(b => b.MinPoint.Y);
        var picks = new List<(Inv.DrawingCurve Curve, double X, double Y)>();
        foreach (double level in levelsMm)
        {
            double y = yRef + level / 10.0 * scale;
            int best = -1;
            double bestX = double.MinValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                bool flat = b.MaxPoint.Y - b.MinPoint.Y < 0.02;
                if (flat && Math.Abs(b.MinPoint.Y - y) < 0.05 && b.MaxPoint.X > bestX) { best = i; bestX = b.MaxPoint.X; }
            }
            if (best < 0)
            {
                Log.Warn($"Drawing: no edge at section level {level} mm; chain skipped.");
                return 0;
            }
            picks.Add((curves[best], boxes[best].MaxPoint.X - 0.2, boxes[best].MinPoint.Y));
        }

        double xText = picks.Max(p => p.X) + offset;
        var dimensions = sheet.DrawingDimensions.GeneralDimensions;
        int made = 0;
        for (int i = 0; i + 1 < picks.Count; i++)
        {
            var a = picks[i];
            var b = picks[i + 1];
            if (Math.Abs(b.Y - a.Y) < 0.05) continue; // hai mức trùng nhau
            dimensions.AddLinear(tg.CreatePoint2d(xText, (a.Y + b.Y) / 2),
                sheet.CreateGeometryIntent(a.Curve, tg.CreatePoint2d(a.X, a.Y)), sheet.CreateGeometryIntent(b.Curve, tg.CreatePoint2d(b.X, b.Y)),
                Inv.DimensionTypeEnum.kVerticalDimensionType, true, Type.Missing, Type.Missing);
            made++;
        }
        return made;
    }

    /// <summary>Các curve của view cùng RangeBox; bỏ curve nằm ngoài khung view (đường cắt của sketch).</summary>
    private static (List<Inv.DrawingCurve> Curves, List<Inv.Box2d> Boxes) ViewCurves(Inv.DrawingView view)
    {
        double limit = view.Center.Y + view.Height / 2 + 0.01;
        var curves = new List<Inv.DrawingCurve>();
        var boxes = new List<Inv.Box2d>();
        foreach (Inv.DrawingCurve c in view.DrawingCurves)
        {
            var box = c.Evaluator2D.RangeBox;
            if (box.MaxPoint.Y > limit) continue;
            curves.Add(c);
            boxes.Add(box);
        }
        return (curves, boxes);
    }

    private static int Extreme(List<Inv.Box2d> boxes, Func<Inv.Box2d, double> key)
    {
        int best = 0;
        double bestKey = double.MinValue;
        for (int i = 0; i < boxes.Count; i++)
        {
            double k = key(boxes[i]);
            if (k > bestKey) { bestKey = k; best = i; }
        }
        return best;
    }

    // ---------------------------------------------------------------- PDF

    private void ExportPdf(Inv.DrawingDocument dd, string path) => ExportWithTranslator(dd, path, PdfTranslatorId);

    private void ExportDwg(Inv.DrawingDocument dd, string path) => ExportWithTranslator(dd, path, DwgTranslatorId);

    private void ExportWithTranslator(Inv.DrawingDocument dd, string path, string translatorId)
    {
        var translator = (Inv.TranslatorAddIn)_app.ApplicationAddIns.ItemById[translatorId];
        if (!translator.Activated) translator.Activate();
        var context = _app.TransientObjects.CreateTranslationContext();
        context.Type = Inv.IOMechanismEnum.kFileBrowseIOMechanism;
        var options = _app.TransientObjects.CreateNameValueMap();
        var medium = _app.TransientObjects.CreateDataMedium();
        medium.FileName = path;
        translator.SaveCopyAs((Inv._Document)dd, context, options, medium);
    }
}
